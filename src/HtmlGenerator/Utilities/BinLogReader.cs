using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
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
        /// <summary>
        /// Binlog reader does not handle concurrent accesses appropriately so handle it here
        /// </summary>
        private static readonly ConcurrentDictionary<string, Lazy<List<CompilerInvocation>>> m_binlogInvocationMap
            = new ConcurrentDictionary<string, Lazy<List<CompilerInvocation>>>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<CompilerInvocation> ExtractInvocations(string binLogFilePath)
        {
            // Normalize the path
            binLogFilePath = Path.GetFullPath(binLogFilePath);

            if (!File.Exists(binLogFilePath))
            {
                throw new FileNotFoundException(binLogFilePath);
            }

            var lazyResult = m_binlogInvocationMap.GetOrAdd(binLogFilePath, new Lazy<List<CompilerInvocation>>(() =>
            {
                if (binLogFilePath.EndsWith(".buildlog", StringComparison.OrdinalIgnoreCase))
                {
                    return ExtractInvocationsFromBuild(binLogFilePath);
                }

                var invocations = new List<CompilerInvocation>();
                var reader = new Microsoft.Build.Logging.StructuredLogger.BinLogReader();
                var taskIdToInvocationMap = new Dictionary<(int, int), CompilerInvocation>();

                void TryGetInvocationFromEvent(object sender, BuildEventArgs args)
                {
                    var invocation = TryGetInvocationFromRecord(args, taskIdToInvocationMap);
                    if (invocation != null)
                    {
                        invocation.SolutionRoot = Path.GetDirectoryName(binLogFilePath);
                        invocations.Add(invocation);
                    }
                }

                reader.TargetStarted += TryGetInvocationFromEvent;
                reader.MessageRaised += TryGetInvocationFromEvent;

                reader.Replay(binLogFilePath);

                return invocations;
            }));

            var result = lazyResult.Value;

            return result;
        }

        private static List<CompilerInvocation> ExtractInvocationsFromBuild(string logFilePath)
        {
            var build = Microsoft.Build.Logging.StructuredLogger.Serialization.Read(logFilePath);
            var invocations = new List<CompilerInvocation>();
            build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(t =>
            {
                var invocation = TryGetInvocationFromTask(t);
                if (invocation != null)
                {
                    invocations.Add(invocation);
                }
            });

            return invocations;
        }

        private static CompilerInvocation TryGetInvocationFromRecord(BuildEventArgs args, Dictionary<(int, int), CompilerInvocation> taskIdToInvocationMap)
        {
            int targetId = args.BuildEventContext?.TargetId ?? -1;
            int projectId = args.BuildEventContext?.ProjectInstanceId ?? -1;
            if (targetId < 0)
            {
                return null;
            }

            var targetStarted = args as TargetStartedEventArgs;
            if (targetStarted != null && targetStarted.TargetName == "CoreCompile")
            {
                var invocation = new CompilerInvocation();
                taskIdToInvocationMap[(targetId, projectId)] = invocation;
                invocation.ProjectFilePath = targetStarted.ProjectFile;
                return null;
            }

            var commandLine = GetCommandLineFromEventArgs(args, out var language);
            if (commandLine == null)
            {
                return null;
            }

            CompilerInvocation compilerInvocation;
            if (taskIdToInvocationMap.TryGetValue((targetId, projectId), out compilerInvocation))
            {
                compilerInvocation.Language = language == CompilerKind.CSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic;
                compilerInvocation.CommandLineArguments = commandLine;
                Populate(compilerInvocation);
                taskIdToInvocationMap.Remove((targetId, projectId));
            }

            return compilerInvocation;
        }

        private static void Populate(CompilerInvocation compilerInvocation)
        {
            if (compilerInvocation.Language == LanguageNames.CSharp)
            {
                try
                {
                    var outputFileName = compilerInvocation.Parsed.OutputFileName;
                    if (!string.IsNullOrEmpty(outputFileName))
                    {
                        compilerInvocation.OutputAssemblyPath = compilerInvocation.Parsed.GetOutputFilePath(outputFileName);
                    }
                }
                catch (Exception ex)
                {
                    // Can happen when the command line uses response files (@file.rsp) that only
                    // exist on the machine that produced the binlog (e.g. a Linux build read on
                    // Windows). The assembly path will be left null; metadata extraction is skipped
                    // gracefully downstream.
                    Log.Exception(ex, $"Could not determine output assembly path for: {compilerInvocation.ProjectFilePath}", isSevere: false);
                }
            }
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
            return new CompilerInvocation
            {
                Language = language,
                CommandLineArguments = commandLine,
                ProjectFilePath = task.GetNearestParent<Microsoft.Build.Logging.StructuredLogger.Project>()?.ProjectFile
            };
        }

        public static string TrimCompilerExeFromCommandLine(string commandLine, CompilerKind language)
        {
            int occurrence = -1;
            int trimLength = 0;

            if (language == CompilerKind.CSharp)
            {
                // Windows: path ends with csc.exe (e.g. "C:\...\csc.exe ")
                occurrence = commandLine.IndexOf("csc.exe ", StringComparison.OrdinalIgnoreCase);
                trimLength = "csc.exe ".Length;

                // Linux/macOS: path ends with csc.dll (e.g. "/path/to/csc.dll ")
                if (occurrence < 0)
                {
                    occurrence = commandLine.IndexOf("csc.dll ", StringComparison.OrdinalIgnoreCase);
                    trimLength = "csc.dll ".Length;
                }
            }
            else if (language == CompilerKind.VisualBasic)
            {
                occurrence = commandLine.IndexOf("vbc.exe ", StringComparison.OrdinalIgnoreCase);
                trimLength = "vbc.exe ".Length;

                if (occurrence < 0)
                {
                    occurrence = commandLine.IndexOf("vbc.dll ", StringComparison.OrdinalIgnoreCase);
                    trimLength = "vbc.dll ".Length;
                }
            }

            if (occurrence > -1)
            {
                commandLine = commandLine.Substring(occurrence + trimLength);
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
    }
}