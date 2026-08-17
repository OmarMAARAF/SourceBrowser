using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
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
        // Caches the parsed result per file so a binlog is only read once, and so concurrent callers don't race the reader.
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

            // The compiler token can appear as "csc.exe " or as a quoted "csc.dll" (SDK/Linux builds
            // invoke it via dotnet exec), so both extensions and an optional closing quote are handled.
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

    }
}