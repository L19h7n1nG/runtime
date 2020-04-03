// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using Internal.CommandLine;
using System.Linq;
using System.IO;

namespace ILCompiler
{
    internal class Program
    {
        private const string DefaultSystemModule = "System.Private.CoreLib";

        private CommandLineOptions _commandLineOptions;
        public TargetOS _targetOS;
        public TargetArchitecture _targetArchitecture;
        public OptimizationMode _optimizationMode;
        private Dictionary<string, string> _inputFilePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _unrootedInputFilePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _referenceFilePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Program(CommandLineOptions commandLineOptions)
        {
            _commandLineOptions = commandLineOptions;
        }

        private void InitializeDefaultOptions()
        {
            // We could offer this as a command line option, but then we also need to
            // load a different RyuJIT, so this is a future nice to have...
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _targetOS = TargetOS.Windows;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _targetOS = TargetOS.Linux;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                _targetOS = TargetOS.OSX;
            else
                throw new NotImplementedException();

            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X86:
                    _targetArchitecture = TargetArchitecture.X86;
                    break;
                case Architecture.X64:
                    _targetArchitecture = TargetArchitecture.X64;
                    break;
                case Architecture.Arm:
                    _targetArchitecture = TargetArchitecture.ARM;
                    break;
                case Architecture.Arm64:
                    _targetArchitecture = TargetArchitecture.ARM64;
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private void ProcessCommandLine()
        {
            AssemblyName name = typeof(Program).GetTypeInfo().Assembly.GetName();

            if (_commandLineOptions.WaitForDebugger)
            {
                Console.WriteLine(SR.WaitingForDebuggerAttach);
                Console.ReadLine();
            }

            if (_commandLineOptions.CompileBubbleGenerics)
            {
                if (!_commandLineOptions.CompositeOrInputBubble)
                {
                    Console.WriteLine(SR.WarningIgnoringBubbleGenerics);
                    _commandLineOptions.CompileBubbleGenerics = false;
                }
            }

            _optimizationMode = OptimizationMode.None;
            if (_commandLineOptions.OptimizeSpace)
            {
                if (_commandLineOptions.OptimizeTime)
                    Console.WriteLine(SR.WarningOverridingOptimizeSpace);
                _optimizationMode = OptimizationMode.PreferSize;
            }
            else if (_commandLineOptions.OptimizeTime)
                _optimizationMode = OptimizationMode.PreferSpeed;
            else if (_commandLineOptions.Optimize)
                _optimizationMode = OptimizationMode.Blended;

            foreach (var input in _commandLineOptions.InputFilePaths ?? Enumerable.Empty<FileInfo>())
                Helpers.AppendExpandedPaths(_inputFilePaths, input.FullName, true);

            foreach (var input in _commandLineOptions.UnrootedInputFilePaths ?? Enumerable.Empty<FileInfo>())
                Helpers.AppendExpandedPaths(_unrootedInputFilePaths, input.FullName, true);

            foreach (var reference in _commandLineOptions.Reference ?? Enumerable.Empty<string>())
                Helpers.AppendExpandedPaths(_referenceFilePaths, reference, false);
        }

        private int Run()
        {
            InitializeDefaultOptions();

            ProcessCommandLine();

            if (_commandLineOptions.OutputFilePath == null)
                throw new CommandLineException(SR.MissingOutputFile);

            //
            // Set target Architecture and OS
            //
            if (_commandLineOptions.TargetArch != null)
            {
                if (_commandLineOptions.TargetArch.Equals("x86", StringComparison.OrdinalIgnoreCase))
                    _targetArchitecture = TargetArchitecture.X86;
                else if (_commandLineOptions.TargetArch.Equals("x64", StringComparison.OrdinalIgnoreCase))
                    _targetArchitecture = TargetArchitecture.X64;
                else if (_commandLineOptions.TargetArch.Equals("arm", StringComparison.OrdinalIgnoreCase))
                    _targetArchitecture = TargetArchitecture.ARM;
                else if (_commandLineOptions.TargetArch.Equals("armel", StringComparison.OrdinalIgnoreCase))
                    _targetArchitecture = TargetArchitecture.ARM;
                else if (_commandLineOptions.TargetArch.Equals("arm64", StringComparison.OrdinalIgnoreCase))
                    _targetArchitecture = TargetArchitecture.ARM64;
                else
                    throw new CommandLineException(SR.TargetArchitectureUnsupported);
            }
            if (_commandLineOptions.TargetOS != null)
            {
                if (_commandLineOptions.TargetOS.Equals("windows", StringComparison.OrdinalIgnoreCase))
                    _targetOS = TargetOS.Windows;
                else if (_commandLineOptions.TargetOS.Equals("linux", StringComparison.OrdinalIgnoreCase))
                    _targetOS = TargetOS.Linux;
                else if (_commandLineOptions.TargetOS.Equals("osx", StringComparison.OrdinalIgnoreCase))
                    _targetOS = TargetOS.OSX;
                else
                    throw new CommandLineException(SR.TargetOSUnsupported);
            }

            using (PerfEventSource.StartStopEvents.CompilationEvents())
            {
                ICompilation compilation;
                using (PerfEventSource.StartStopEvents.LoadingEvents())
                {
                    //
                    // Initialize type system context
                    //

                    SharedGenericsMode genericsMode = SharedGenericsMode.CanonicalReferenceTypes;

                    var targetDetails = new TargetDetails(_targetArchitecture, _targetOS, TargetAbi.CoreRT, SimdVectorLength.None);
                    CompilerTypeSystemContext typeSystemContext = new ReadyToRunCompilerContext(targetDetails, genericsMode);

                    //
                    // TODO: To support our pre-compiled test tree, allow input files that aren't managed assemblies since
                    // some tests contain a mixture of both managed and native binaries.
                    //
                    // See: https://github.com/dotnet/corert/issues/2785
                    //
                    // When we undo this this hack, replace this foreach with
                    //  typeSystemContext.InputFilePaths = _inputFilePaths;
                    //
                    Dictionary<string, string> allInputFilePaths = new Dictionary<string, string>();
                    Dictionary<string, string> inputFilePaths = new Dictionary<string, string>();
                    List<ModuleDesc> referenceableModules = new List<ModuleDesc>();
                    foreach (var inputFile in _inputFilePaths)
                    {
                        try
                        {
                            var module = typeSystemContext.GetModuleFromPath(inputFile.Value);
                            allInputFilePaths.Add(inputFile.Key, inputFile.Value);
                            inputFilePaths.Add(inputFile.Key, inputFile.Value);
                            referenceableModules.Add(module);
                        }
                        catch (TypeSystemException.BadImageFormatException)
                        {
                            // Keep calm and carry on.
                        }
                    }

                    Dictionary<string, string> unrootedInputFilePaths = new Dictionary<string, string>();
                    foreach (var unrootedInputFile in _unrootedInputFilePaths)
                    {
                        try
                        {
                            var module = typeSystemContext.GetModuleFromPath(unrootedInputFile.Value);
                            if (!allInputFilePaths.ContainsKey(unrootedInputFile.Key))
                            {
                                allInputFilePaths.Add(unrootedInputFile.Key, unrootedInputFile.Value);
                                unrootedInputFilePaths.Add(unrootedInputFile.Key, unrootedInputFile.Value);
                                referenceableModules.Add(module);
                            }
                        }
                        catch (TypeSystemException.BadImageFormatException)
                        {
                            // Keep calm and carry on.
                        }
                    }

                    typeSystemContext.InputFilePaths = allInputFilePaths;
                    typeSystemContext.ReferenceFilePaths = _referenceFilePaths;

                    List<EcmaModule> inputModules = new List<EcmaModule>();
                    List<EcmaModule> rootingModules = new List<EcmaModule>();
                    HashSet<ModuleDesc> versionBubbleModulesHash = new HashSet<ModuleDesc>();

                    foreach (var inputFile in inputFilePaths)
                    {
                        EcmaModule module = typeSystemContext.GetModuleFromPath(inputFile.Value);
                        inputModules.Add(module);
                        rootingModules.Add(module);
                        versionBubbleModulesHash.Add(module);

                        if (!_commandLineOptions.CompositeOrInputBubble)
                        {
                            break;
                        }
                    }

                    foreach (var unrootedInputFile in unrootedInputFilePaths)
                    {
                        EcmaModule module = typeSystemContext.GetModuleFromPath(unrootedInputFile.Value);
                        inputModules.Add(module);
                        versionBubbleModulesHash.Add(module);
                    }

                    string systemModuleName = _commandLineOptions.SystemModule ?? DefaultSystemModule;
                    typeSystemContext.SetSystemModule((EcmaModule)typeSystemContext.GetModuleForSimpleName(systemModuleName));

                    if (typeSystemContext.InputFilePaths.Count == 0)
                        throw new CommandLineException(SR.NoInputFiles);

                    //
                    // Initialize compilation group and compilation roots
                    //

                    // Single method mode?
                    MethodDesc singleMethod = CheckAndParseSingleMethodModeArguments(typeSystemContext);

                    var logger = new Logger(Console.Out, _commandLineOptions.Verbose);

                    List<string> mibcFiles = new List<string>();
                    if (_commandLineOptions.Mibc != null)
                    {
                        foreach (var file in _commandLineOptions.Mibc)
                        {
                            mibcFiles.Add(file.FullName);
                        }
                    }

                    foreach (var referenceFile in _referenceFilePaths.Values)
                    {
                        try
                        {
                            EcmaModule module = typeSystemContext.GetModuleFromPath(referenceFile);
                            if (versionBubbleModulesHash.Contains(module))
                            {
                                // Ignore reference assemblies that have also been passed as inputs
                                continue;
                            }
                            referenceableModules.Add(module);
                            if (_commandLineOptions.InputBubble)
                            {
                                // In large version bubble mode add reference paths to the compilation group
                                versionBubbleModulesHash.Add(module);
                            }
                        }
                        catch { } // Ignore non-managed pe files
                    }

                    List<ModuleDesc> versionBubbleModules = new List<ModuleDesc>(versionBubbleModulesHash);

                    if (!_commandLineOptions.Composite && inputModules.Count != 1)
                    {
                        throw new Exception(string.Format(SR.ErrorMultipleInputFilesCompositeModeOnly, string.Join("; ", inputModules)));
                    }

                    ReadyToRunCompilationModuleGroupBase compilationGroup;
                    List<ICompilationRootProvider> compilationRoots = new List<ICompilationRootProvider>();
                    if (singleMethod != null)
                    {
                        // Compiling just a single method
                        compilationGroup = new SingleMethodCompilationModuleGroup(
                            typeSystemContext,
                            _commandLineOptions.Composite,
                            _commandLineOptions.InputBubble,
                            inputModules,
                            versionBubbleModules,
                            _commandLineOptions.CompileBubbleGenerics,
                            singleMethod);
                        compilationRoots.Add(new SingleMethodRootProvider(singleMethod));
                    }
                    else
                    {
                        // Single assembly compilation.
                        compilationGroup = new ReadyToRunSingleAssemblyCompilationModuleGroup(
                            typeSystemContext,
                            _commandLineOptions.Composite,
                            _commandLineOptions.InputBubble,
                            inputModules,
                            versionBubbleModules,
                            _commandLineOptions.CompileBubbleGenerics);
                    }

                    // Examine profile guided information as appropriate
                    ProfileDataManager profileDataManager =
                        new ProfileDataManager(logger,
                        referenceableModules,
                        inputModules,
                        versionBubbleModules,
                        _commandLineOptions.CompileBubbleGenerics ? inputModules[0] : null,
                        mibcFiles,
                        typeSystemContext,
                        compilationGroup);

                    if (_commandLineOptions.Partial)
                        compilationGroup.ApplyProfilerGuidedCompilationRestriction(profileDataManager);
                    else
                        compilationGroup.ApplyProfilerGuidedCompilationRestriction(null);

                    if (singleMethod == null)
                    {
                        // For non-single-method compilations add compilation roots.
                        foreach (var module in rootingModules)
                        {
                            compilationRoots.Add(new ReadyToRunRootProvider(
                                module,
                                profileDataManager,
                                profileDrivenPartialNGen: _commandLineOptions.Partial));

                            if (!_commandLineOptions.CompositeOrInputBubble)
                            {
                                break;
                            }
                        }
                    }

                    //
                    // Compile
                    //

                    ReadyToRunCodegenCompilationBuilder builder = new ReadyToRunCodegenCompilationBuilder(
                        typeSystemContext, compilationGroup, allInputFilePaths.Values);
                    string compilationUnitPrefix = "";
                    builder.UseCompilationUnitPrefix(compilationUnitPrefix);

                    ILProvider ilProvider = new ReadyToRunILProvider();

                    DependencyTrackingLevel trackingLevel = _commandLineOptions.DgmlLogFileName == null ?
                        DependencyTrackingLevel.None : (_commandLineOptions.GenerateFullDgmlLog ? DependencyTrackingLevel.All : DependencyTrackingLevel.First);

                    builder
                        .UseIbcTuning(_commandLineOptions.Tuning)
                        .UseResilience(_commandLineOptions.Resilient)
                        .UseMapFile(_commandLineOptions.Map)
                        .UseParallelism(_commandLineOptions.Parallelism)
                        .UseJitPath(_commandLineOptions.JitPath)
                        .UseILProvider(ilProvider)
                        .UseBackendOptions(_commandLineOptions.CodegenOptions)
                        .UseLogger(logger)
                        .UseDependencyTracking(trackingLevel)
                        .UseCompilationRoots(compilationRoots)
                        .UseOptimizationMode(_optimizationMode);

                    compilation = builder.ToCompilation();

                }
                compilation.Compile(_commandLineOptions.OutputFilePath.FullName);

                if (_commandLineOptions.DgmlLogFileName != null)
                    compilation.WriteDependencyLog(_commandLineOptions.DgmlLogFileName.FullName);
            }

            return 0;
        }

        private TypeDesc FindType(CompilerTypeSystemContext context, string typeName)
        {
            ModuleDesc systemModule = context.SystemModule;

            TypeDesc foundType = systemModule.GetTypeByCustomAttributeTypeName(typeName, false, (typeDefName, module, throwIfNotFound) =>
            {
                return (MetadataType)context.GetCanonType(typeDefName)
                    ?? CustomAttributeTypeNameParser.ResolveCustomAttributeTypeDefinitionName(typeDefName, module, throwIfNotFound);
            });
            if (foundType == null)
                throw new CommandLineException(string.Format(SR.TypeNotFound, typeName));

            return foundType;
        }

        private MethodDesc CheckAndParseSingleMethodModeArguments(CompilerTypeSystemContext context)
        {
            if (_commandLineOptions.SingleMethodName == null && _commandLineOptions.SingleMethodTypeName == null && _commandLineOptions.SingleMethodGenericArgs == null)
                return null;

            if (_commandLineOptions.SingleMethodName == null || _commandLineOptions.SingleMethodTypeName == null)
                throw new CommandLineException(SR.TypeAndMethodNameNeeded);

            TypeDesc owningType = FindType(context, _commandLineOptions.SingleMethodTypeName);

            // TODO: allow specifying signature to distinguish overloads
            MethodDesc method = owningType.GetMethod(_commandLineOptions.SingleMethodName, null);
            if (method == null)
                throw new CommandLineException(string.Format(SR.MethodNotFoundOnType, _commandLineOptions.SingleMethodName, _commandLineOptions.SingleMethodTypeName));

            if (method.HasInstantiation != (_commandLineOptions.SingleMethodGenericArgs != null) ||
                (method.HasInstantiation && (method.Instantiation.Length != _commandLineOptions.SingleMethodGenericArgs.Length)))
            {
                throw new CommandLineException(
                    string.Format(SR.GenericArgCountMismatch, method.Instantiation.Length, _commandLineOptions.SingleMethodName, _commandLineOptions.SingleMethodTypeName));
            }

            if (method.HasInstantiation)
            {
                List<TypeDesc> genericArguments = new List<TypeDesc>();
                foreach (var argString in _commandLineOptions.SingleMethodGenericArgs)
                    genericArguments.Add(FindType(context, argString));
                method = method.MakeInstantiatedMethod(genericArguments.ToArray());
            }

            return method;
        }

        private static bool DumpReproArguments(CodeGenerationFailedException ex)
        {
            Console.WriteLine(SR.DumpReproInstructions);

            MethodDesc failingMethod = ex.Method;

            var formatter = new CustomAttributeTypeNameFormatter((IAssemblyDesc)failingMethod.Context.SystemModule);

            Console.Write($"--singlemethodtypename \"{formatter.FormatName(failingMethod.OwningType, true)}\"");
            Console.Write($" --singlemethodname {failingMethod.Name}");

            for (int i = 0; i < failingMethod.Instantiation.Length; i++)
                Console.Write($" --singlemethodgenericarg \"{formatter.FormatName(failingMethod.Instantiation[i], true)}\"");

            Console.WriteLine();
            return false;
        }

        public static async Task<int> Main(string[] args)
        {
            var command = CommandLineOptions.RootCommand();
            command.Handler = CommandHandler.Create<CommandLineOptions>((CommandLineOptions options) => InnerMain(options));
            return await command.InvokeAsync(args);
        }

        private static int InnerMain(CommandLineOptions buildOptions)
        {
#if DEBUG
            try
            {
                return new Program(buildOptions).Run();
            }
            catch (CodeGenerationFailedException ex) when (DumpReproArguments(ex))
            {
                throw new NotSupportedException(); // Unreachable
            }
#else
            try
            {
                return new Program(buildOptions).Run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(string.Format(SR.ProgramError, e.Message));
                Console.Error.WriteLine(e.ToString());
                return 1;
            }
#endif
        }
    }
}
