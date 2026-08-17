using Microsoft.Build.Locator;
using Microsoft.SourceBrowser.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Program
    {
        private static List<string> assymbliesToRead = new List<string>(new [] {
            "Ard.Highway.Gateway.Common",
            "Ard.Highway.GateWay.Fwk",
            "Ard.Highway.GateWay.UnitTests",
            "Sgcib.Mark.Ard.Highway.Launcher"});
        private static async Task Main(string[] args)
        {
            var options = CommandLineOptions.Parse(args);

            if (options.Projects.Count == 0)
            {
                PrintUsage();
                return;
            }

            Paths.SolutionDestinationFolder = options.SolutionDestinationFolder;
            SolutionGenerator.LoadPlugins = options.LoadPlugins;
            SolutionGenerator.ExcludeTests = options.ExcludeTests;

            AssertTraceListener.Register();
            AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler.HandleFirstChanceException;

            // Loads the real MSBuild toolset so all targets/SDKs resolve as if a real build were happening.
            // Picking the highest version explicitly works around MSBuildLocator sometimes resolving v16 instead of v17.
            var msbuild = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).First();
            MSBuildLocator.RegisterInstance(msbuild);
            Log.Message($"Registered MSBuild is: {msbuild.Version} from '{msbuild.MSBuildPath}'");

            if (Paths.SolutionDestinationFolder == null)
            {
                Paths.SolutionDestinationFolder = Path.Combine(Microsoft.SourceBrowser.Common.Paths.BaseAppFolder, "index");
            }

            var websiteDestination = Paths.SolutionDestinationFolder;

            // Warning, this will delete and recreate your destination folder
            Paths.PrepareDestinationFolder(options.Force);

            Paths.SolutionDestinationFolder = Path.Combine(Paths.SolutionDestinationFolder, "index"); //The actual index files need to be written to the "index" subdirectory

            Directory.CreateDirectory(Paths.SolutionDestinationFolder);

            Log.ErrorLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.ErrorLogFile);
            Log.MessageLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.MessageLogFile);

            using (Disposable.Timing("Generating website"))
            {
                var federation = new Federation();

                if (!options.NoBuiltInFederations)
                {
                    federation.AddFederations(Federation.DefaultFederatedIndexUrls);
                }

                federation.AddFederations(options.Federations);

                foreach (var entry in options.OfflineFederations)
                {
                    if (!File.Exists(entry.Value))
                    {
                        Log.Exception($"Assembly file {entry.Value} was not found", false);
                        continue;
                    }
                    federation.AddFederation(entry.Key, entry.Value);
                }

                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        Console.WriteLine("Cancellation requested...");
                        cts.Cancel();
                        eventArgs.Cancel = true;
                    };

                    await IndexSolutionsAsync(options.Projects, options.Properties, federation, options.ServerPathMappings, options.PluginBlacklist, cts.Token, options.DoNotIncludeReferencedProjects, options.RootPath,
                        options.IncludeSourceGeneratedDocuments, options.BinlogReplacementRootPath);
                }
                FinalizeProjects(options.EmitAssemblyList, federation);
                WebsiteFinalizer.Finalize(websiteDestination, options.EmitAssemblyList, federation);
            }
            Log.Close();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: HtmlGenerator "
                + "[/out:<outputdirectory>] "
                + "[/force] "
                + "[/useplugins] "
                + "[/noplugins] "
                + "[/noplugin:Git] "
                + "<pathtosolution1.csproj|vbproj|sln|slnx|binlog|buildlog|dll|exe> [more solutions/projects..] "
                + "[/root:<root folder to enable relative .sln/.slnx folders>] "
                + "[/in:<filecontaingprojectlist>] "
                + "[/nobuiltinfederations] "
                + "[/offlinefederation:server=assemblyListFile] "
                + "[/assemblylist]"
                + "[/excludetests]" 
                + "[/excludeSourceGeneratedDocuments]"
                + "[/replacementroot:<new root folder to replace original projects root in binary log>] "
                + "Plugins are now off by default.");
        }
        
        // Rewrites paths in compiler invocations extracted from a .binlog built on another machine so they
        // point at where the sources actually live locally. Uses teamcity_build_checkoutDir from the binlog
        // metadata as the original root, or infers it from the longest common prefix of the project paths.
        private static GenerateFromBuildLog.CompilerInvocation[] ReplaceRootInInvocations(
            IEnumerable<GenerateFromBuildLog.CompilerInvocation> invocations,
            string oldRoot,
            string newRoot)
        {
            var list = invocations.ToArray();
            if (list.Length == 0) return list;

            if (string.IsNullOrEmpty(oldRoot))
            {
                oldRoot = ComputeCommonRootFromProjectPaths(list);
                if (string.IsNullOrEmpty(oldRoot))
                {
                    return list;
                }
            }

            // If oldRoot's folder exists as a subdirectory of newRoot, target that instead (multiple projects checked out under one shared folder).
            var resolvedRoot = ResolveReplacementRootTarget(newRoot, oldRoot);
            if (string.Equals(oldRoot, resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return list;
            }

            var oldRootRegex = new Regex(Regex.Escape(oldRoot), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var escapedNewRoot = resolvedRoot.Replace("$", "$$");
            var result = new GenerateFromBuildLog.CompilerInvocation[list.Length];
            Parallel.For(0, list.Length, i =>
            {
                var inv = list[i];
                var cmdLine = inv.CommandLineArguments ?? string.Empty;
                var replacedCmdLine = cmdLine.IndexOf(oldRoot, StringComparison.OrdinalIgnoreCase) >= 0
                    ? oldRootRegex.Replace(cmdLine, escapedNewRoot)
                    : cmdLine;

                var replaced = new GenerateFromBuildLog.CompilerInvocation
                {
                    ProjectFilePath      = ReplaceRootInPath(inv.ProjectFilePath, oldRoot, resolvedRoot),
                    CommandLineArguments = replacedCmdLine,
                    SolutionRoot         = inv.SolutionRoot,
                    TypeScriptFiles      = inv.TypeScriptFiles,
                    Language             = inv.Language,
                };

                // The binlog-recorded output path may point at a foreign filesystem, so re-derive it
                // from the replaced command line rather than just prefix-replacing.
                replaced.OutputAssemblyPath =
                    RecomputeOutputAssemblyPath(replaced)
                    ?? ReplaceRootInPath(inv.OutputAssemblyPath, oldRoot, resolvedRoot);

                result[i] = replaced;
            });

            return result;
        }

        private static string ResolveReplacementRootTarget(string newRoot, string oldRoot)
        {
            var oldFolder = Path.GetFileName(oldRoot.TrimEnd('\\', '/'));
            var replacementFolderName = new DirectoryInfo(newRoot).Name;
            if (string.Equals(oldFolder, replacementFolderName, StringComparison.OrdinalIgnoreCase))
                return newRoot;
            var candidate = Path.Combine(newRoot, oldFolder);
            return Directory.Exists(candidate) ? candidate : newRoot;
        }

        // Longest common directory prefix of all project paths, used when the binlog has no recorded checkout directory.
        private static string ComputeCommonRootFromProjectPaths(GenerateFromBuildLog.CompilerInvocation[] invocations)
        {
            string commonPrefix = null;

            foreach (var inv in invocations)
            {
                var projectPath = inv.ProjectFilePath;
                if (string.IsNullOrEmpty(projectPath) || projectPath == "-") continue;
                var dir = Path.GetDirectoryName(projectPath);
                if (string.IsNullOrEmpty(dir)) continue;
                if (commonPrefix == null)
                {
                    commonPrefix = dir;
                    continue;
                }
                commonPrefix = GetLongestCommonDirectoryPrefix(commonPrefix, dir);
                if (string.IsNullOrEmpty(commonPrefix))
                {
                    return null;
                }
            }
            return commonPrefix;
        }

        
        //Returns the longest common prefix of two paths. Both paths are compared case-insensitively (Windows-friendly).
        private static string GetLongestCommonDirectoryPrefix(string path1, string path2)
        {
            int minLen = Math.Min(path1.Length, path2.Length);
            int lastSep = -1;

            for (int i = 0; i < minLen; i++)
            {
                var c1 = char.ToUpperInvariant(path1[i]);
                var c2 = char.ToUpperInvariant(path2[i]);
                // Treat '/' and '\' as equivalent separators
                bool isSep1 = c1 == '\\' || c1 == '/';
                bool isSep2 = c2 == '\\' || c2 == '/';
                if (isSep1 && isSep2)
                {
                    lastSep = i;
                }
                else if (c1 != c2)
                {
                    break;
                }
            }
            // If one path is a prefix of the other and ends at a separator boundary
            if (minLen < Math.Max(path1.Length, path2.Length) && minLen > 0)
            {
                var longer = path1.Length > path2.Length ? path1 : path2;
                if (minLen == lastSep + 1 || (longer[minLen] == '\\' || longer[minLen] == '/'))
                {
                    // The shorter path is the common prefix (it's a complete directory)
                    var shorter = path1.Length <= path2.Length ? path1 : path2;
                    return shorter.TrimEnd('\\', '/');
                }
            }
            return lastSep >= 0 ? path1.Substring(0, lastSep) : null;
        }

        private static string ReplaceRootInPath(string projectPath, string oldRoot, string newRoot)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                return projectPath;
            }
            var normalizedPath = Path.GetFullPath(projectPath);
            var normalizedOldRoot = Path.GetFullPath(oldRoot).TrimEnd('\\', '/');
            if (!Paths.IsOrContains(normalizedOldRoot, normalizedPath))
            {
                return projectPath;
            }
            var relativePath = Paths.MakeRelativeToFolder(normalizedPath, normalizedOldRoot);
            return Path.GetFullPath(Path.Combine(newRoot, relativePath));
        }

        // Re-parses the replaced command line to find where the output assembly lives locally.
        // Returns null if it can't be determined, in which case the caller falls back to a prefix replacement.
        private static string RecomputeOutputAssemblyPath(GenerateFromBuildLog.CompilerInvocation invocation)
        {
            if (string.IsNullOrEmpty(invocation.ProjectFilePath) || invocation.ProjectFilePath == "-")
            {
                return null;
            }

            try
            {
                var outputFileName = invocation.Parsed.OutputFileName;
                if (!string.IsNullOrEmpty(outputFileName))
                {
                    return invocation.Parsed.GetOutputFilePath(outputFileName);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, $"Could not recompute output assembly path after root replacement: {invocation.ProjectFilePath}", isSevere: false);
            }

            return null;
        }

        private static readonly Folder<ProjectSkeleton> mergedSolutionExplorerRoot = new Folder<ProjectSkeleton>();

        private static async Task<IEnumerable<string>> GetAssemblyNamesAsync(string filePath, CancellationToken cancellationToken)
        {
            if (filePath.EndsWith(".binlog", System.StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".buildlog", System.StringComparison.OrdinalIgnoreCase))
            {
                var invocations = BinLogCompilerInvocationsReader.ExtractInvocations(filePath);
                return invocations.Select(i => Path.GetFileNameWithoutExtension(i.Parsed.OutputFileName)).ToArray();
            }

            return await AssemblyNameExtractor.GetAssemblyNamesAsync(filePath, cancellationToken);
        }

        private static async Task IndexSolutionsAsync(
            IEnumerable<string> solutionFilePaths,
            IReadOnlyDictionary<string, string> properties,
            Federation federation,
            IReadOnlyDictionary<string, string> serverPathMappings,
            IEnumerable<string> pluginBlacklist,
            CancellationToken cancellationToken,
            bool doNotIncludeReferencedProjects = false,
            string rootPath = null,
            bool includeSourceGeneratedDocuments = true,
            string binlogReplacementRootPath = null)
        {
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Reading assembly names from " + path))
                {
                    foreach (var assemblyName in await GetAssemblyNamesAsync(path, cancellationToken))
                    {
                        assemblyNames.Add(assemblyName);
                    }
                }
            }

            // Temporary: only index Highway assemblies to speed up test runs.
            assemblyNames.RemoveWhere(n => !assymbliesToRead.Contains(n));

            var processedAssemblyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                var solutionFolder = mergedSolutionExplorerRoot;

                if (rootPath is object)
                {
                    var relativePath = Paths.MakeRelativeToFolder(Path.GetDirectoryName(path), rootPath);
                    var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var segment in segments)
                    {
                        solutionFolder = solutionFolder.GetOrCreateFolder(segment);
                    }
                }

                using (Disposable.Timing("Generating " + path))
                {
                    if (path.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".buildlog", StringComparison.OrdinalIgnoreCase))
                    {
                        var binlogData = BinLogCompilerInvocationsReader.ExtractBinLogData(path);
                        var invocations = binlogData.Invocations.ToArray();
                        if (binlogReplacementRootPath != null)
                        {
                            invocations = ReplaceRootInInvocations(invocations, binlogData.CheckoutDirectory, binlogReplacementRootPath);
                        }

                        // Temporary: mirror the assemblyNames filter so we don't process non-Highway projects.
                        invocations = invocations
                            .Where(inv => assymbliesToRead.Contains(inv.AssemblyName))
                            .ToArray();

                        foreach (var invocation in invocations)
                        {
                            SolutionGenerator.RegisterLocalOutputAssembly(invocation.OutputAssemblyPath);
                        }

                        foreach (var invocation in invocations)
                        {
                            await GenerateFromBuildLog.GenerateInvocationAsync(
                                invocation,
                                cancellationToken,
                                serverPathMappings,
                                processedAssemblyList,
                                assemblyNames,
                                solutionFolder,
                                includeSourceGeneratedDocuments: includeSourceGeneratedDocuments);
                        }

                        continue;
                    }

                    using (var solutionGenerator = await SolutionGenerator.CreateAsync(
                        path,
                        Paths.SolutionDestinationFolder,
                        cancellationToken,
                        properties: properties.ToImmutableDictionary(),
                        federation: federation,
                        serverPathMappings: serverPathMappings,
                        pluginBlacklist: pluginBlacklist,
                        doNotIncludeReferencedProjects: doNotIncludeReferencedProjects,
                        includeSourceGeneratedDocuments: includeSourceGeneratedDocuments))
                    {
                        solutionGenerator.GlobalAssemblyList = assemblyNames;
                        await solutionGenerator.GenerateAsync(cancellationToken, processedAssemblyList, solutionFolder);
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private static void FinalizeProjects(bool emitAssemblyList, Federation federation)
        {
            GenerateLooseFilesProject(Constants.MSBuildFiles, Paths.SolutionDestinationFolder);
            GenerateLooseFilesProject(Constants.TypeScriptFiles, Paths.SolutionDestinationFolder);
            using (Disposable.Timing("Finalizing references"))
            {
                try
                {
                    var solutionFinalizer = new SolutionFinalizer(Paths.SolutionDestinationFolder);
                    solutionFinalizer.FinalizeProjects(emitAssemblyList, federation, mergedSolutionExplorerRoot);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, "Failure while finalizing projects");
                }
            }
        }

        private static void GenerateLooseFilesProject(string projectName, string solutionDestinationPath)
        {
            var projectGenerator = new ProjectGenerator(projectName, solutionDestinationPath);
            projectGenerator.GenerateNonProjectFolder();
        }
    }

    internal static class WebsiteFinalizer
    {
        public static void Finalize(string destinationFolder, bool emitAssemblyList, Federation federation)
        {
            string sourcePath = Assembly.GetEntryAssembly().Location;
            sourcePath = Path.GetDirectoryName(sourcePath);
            string basePath = sourcePath;
            sourcePath = Path.Combine(sourcePath, "Web");
            if (!Directory.Exists(sourcePath))
            {
                return;
            }

            sourcePath = Path.GetFullPath(sourcePath);
            FileUtilities.CopyDirectory(sourcePath, destinationFolder);

            StampOverviewHtmlWithDate(destinationFolder);

            if (emitAssemblyList)
            {
                ToggleSolutionExplorerOff(destinationFolder);
            }

            SetExternalUrlMap(destinationFolder, federation);
        }

        private static void StampOverviewHtmlWithDate(string destinationFolder)
        {
            var source = Path.Combine(destinationFolder, "wwwroot", "overview.html");
            var dst = Path.Combine(destinationFolder, "index", "overview.html");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = StampOverviewHtmlText(text);
                File.WriteAllText(dst, text);
            }
        }

        private static string StampOverviewHtmlText(string text)
        {
            return text.Replace("$(Date)", DateTime.Today.ToString("MMMM d", CultureInfo.InvariantCulture));
        }

        private static void ToggleSolutionExplorerOff(string destinationFolder)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                text = text.Replace("/*USE_SOLUTION_EXPLORER*/true/*USE_SOLUTION_EXPLORER*/", "false");
                File.WriteAllText(dst, text);
            }
        }

        private static void SetExternalUrlMap(string destinationFolder, Federation federation)
        {
            var source = Path.Combine(destinationFolder, "wwwroot/scripts.js");
            var dst = Path.Combine(destinationFolder, "index/scripts.js");
            if (File.Exists(source))
            {
                var sb = new StringBuilder();
                foreach (var server in federation.GetServers())
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(",");
                    }

                    sb.Append("\"");
                    sb.Append(server);
                    sb.Append("\"");
                }

                if (sb.Length > 0)
                {
                    var text = File.ReadAllText(source);
                    text = Regex.Replace(text, @"/\*EXTERNAL_URL_MAP\*/.*/\*EXTERNAL_URL_MAP\*/", sb.ToString());
                    File.WriteAllText(dst, text);
                }
            }
        }
    }
}
