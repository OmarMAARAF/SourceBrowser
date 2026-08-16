using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;
using CompilerInvocation = Microsoft.SourceBrowser.HtmlGenerator.GenerateFromBuildLog.CompilerInvocation;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public enum CompilerKind
    {
        CSharp,
        VisualBasic
    }

    public class BinLogCompilerInvocationsReader
    {
        /// Binlog reader does not handle concurrent accesses appropriately so handle it here.
        /// The cached result stores both compiler invocations and metadata so the file is read once
        private static readonly ConcurrentDictionary<string, Lazy<BinLogExtractionResult>> mBinlogDataMap = new ConcurrentDictionary<string, Lazy<BinLogExtractionResult>>(StringComparer.OrdinalIgnoreCase);
        private const string TeamcityBuildCheckoutDir = "teamcity_build_checkoutDir";

        public sealed class BinLogExtractionResult
        {
            public BinLogExtractionResult(IReadOnlyList<CompilerInvocation> invocations, string checkoutDirectory)
            {
                Invocations = invocations ?? Array.Empty<CompilerInvocation>();
                CheckoutDirectory = checkoutDirectory;
            }
            public IReadOnlyList<CompilerInvocation> Invocations { get; }
            public string CheckoutDirectory { get; }
        }

        public static BinLogExtractionResult ExtractBinLogData(string binLogFilePath)
        {
            binLogFilePath = Path.GetFullPath(binLogFilePath);

            if (!File.Exists(binLogFilePath))
            {
                throw new FileNotFoundException(binLogFilePath);
            }

            var lazyResult = mBinlogDataMap.GetOrAdd(binLogFilePath, new Lazy<BinLogExtractionResult>(() => ExtractFromBuild(binLogFilePath)));
            return lazyResult.Value;
        }

        public static IEnumerable<CompilerInvocation> ExtractInvocations(string binLogFilePath)
        {
            return ExtractBinLogData(binLogFilePath).Invocations;
        }

        private static BinLogExtractionResult ExtractFromBuild(string logFilePath)
        {
            var build = Microsoft.Build.Logging.StructuredLogger.Serialization.Read(logFilePath);
            var solutionRoot = Path.GetDirectoryName(logFilePath);
            var invocations = new List<CompilerInvocation>();
            build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(t =>
            {
                var invocation = TryGetInvocationFromTask(t);
                if (invocation != null)
                {
                    invocation.SolutionRoot = solutionRoot;
                    invocations.Add(invocation);
                }
            });
            var checkoutDir = build
                .FindChildrenRecursive<Property>()
                .FirstOrDefault(p => string.Equals(p.Name, TeamcityBuildCheckoutDir, StringComparison.OrdinalIgnoreCase))?.Value;
            Log.Message($"Old checkout directory in binlog is : {checkoutDir}");
            return new BinLogExtractionResult(invocations, checkoutDir);
        }

        private static CompilerInvocation TryGetInvocationFromTask(Microsoft.Build.Logging.StructuredLogger.Task task)
        {
            var name = task.Name;
            if (name != "Csc" && name != "Vbc" || ((task.Parent as Microsoft.Build.Logging.StructuredLogger.Target)?.Name != "CoreCompile"))
            {
                return null;
            }

            var language = name == "Csc" ? LanguageNames.CSharp : LanguageNames.VisualBasic;
            var commandLine = task.CommandLineArguments;
            commandLine = TrimCompilerExeFromCommandLine(commandLine, name == "Csc"
                ? CompilerKind.CSharp
                : CompilerKind.VisualBasic);
            var invocation = new CompilerInvocation
            {
                Language = language,
                CommandLineArguments = commandLine,
                ProjectFilePath = task.GetNearestParent<Microsoft.Build.Logging.StructuredLogger.Project>()?.ProjectFile
            };

            // Mirror the streaming reader so both paths resolve the output assembly path.
            var parsed = invocation.Parsed;
            if (invocation.Language == LanguageNames.CSharp && parsed != null)
            {
                invocation.OutputAssemblyPath = parsed.GetOutputFilePath(parsed.OutputFileName);
            }
            return invocation;
        }

        public static string TrimCompilerExeFromCommandLine(string commandLine, CompilerKind language)
        {
            if (string.IsNullOrEmpty(commandLine))
            {
                return commandLine;
            }

            // The compiler token appears in different forms depending on the OS/build host:
            //   Windows:      C:\...\bin\Roslyn\csc.exe /noconfig ...
            //   Windows (SDK): "C:\...\csc.dll" /noconfig ...
            //   Linux/macOS:  /usr/.../dotnet exec "/usr/.../Roslyn/bincore/csc.dll" /noconfig ...
            // In the .dll forms the path is usually wrapped in quotes, so the character right
            // after "csc.dll"/"vbc.dll" is a double quote rather than a space. Searching for the
            // token followed by a literal space (as was done previously) fails on these binlogs
            // and leaves the "dotnet exec ...csc.dll" prefix in the command line, which then gets
            // misinterpreted as extra source files by the command line parser.
            var compiler = language == CompilerKind.CSharp ? "csc" : "vbc";

            foreach (var extension in new[] { ".exe", ".dll" })
            {
                var token = compiler + extension;
                int occurrence = commandLine.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (occurrence < 0)
                {
                    continue;
                }

                int cut = occurrence + token.Length;

                // Skip a closing quote wrapping the compiler path (e.g. "...csc.dll").
                if (cut < commandLine.Length && commandLine[cut] == '"')
                {
                    cut++;
                }

                // Skip any whitespace separating the compiler path from the first argument.
                while (cut < commandLine.Length && char.IsWhiteSpace(commandLine[cut]))
                {
                    cut++;
                }

                return commandLine.Substring(cut);
            }

            return commandLine;
        }

        public static string GetCommandLineFromEventArgs(BuildEventArgs args, out CompilerKind language)
        {
            var task = args as TaskCommandLineEventArgs;
            language = default;
            if (task == null)
            {
                return null;
            }

            var name = task.TaskName;
            if (name != "Csc" && name != "Vbc")
            {
                return null;
            }

            language = name == "Csc" ? CompilerKind.CSharp : CompilerKind.VisualBasic;
            var commandLine = task.CommandLine;
            commandLine = TrimCompilerExeFromCommandLine(commandLine, language);
            return commandLine;
        }


        /// Extracts the original checkout directory from binlog metadata.
        public static string ExtractCheckoutDirectory(string binLogFilePath)
        {
            try
            {
                return ExtractBinLogData(binLogFilePath).CheckoutDirectory;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, $"Failed to extract checkout directory from binlog: {binLogFilePath}", isSevere: false);
                return null;
            }
        }
    }
}