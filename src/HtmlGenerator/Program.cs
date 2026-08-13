using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;
using System;
using System.Collections.Concurrent;
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
            SolutionGenerator.AllowDuplicateAssemblies = options.AllowDuplicateAssemblies;

            AssertTraceListener.Register();
            AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler.HandleFirstChanceException;

            // This loads the real MSBuild from the toolset so that all targets and SDKs can be found
            // as if a real build is happening
            MSBuildLocator.RegisterDefaults();

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
                        options.IncludeSourceGeneratedDocuments, options.BinlogRebasePath);
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
                + "[/allowduplicateassemblies]"
                + "[/rebase:<localreporoot>] "
                + "Plugins are now off by default.");
        }

        /// <summary>
        /// Rewrites paths in compiler invocations extracted from a .binlog that was produced on
        /// a different machine/agent (or a different checkout layout on the same machine) so they
        /// point at where the sources actually live locally.
        ///
        /// <para>
        /// Detection is grounded in the real filesystem: for each invocation the recorded project
        /// file is located under <paramref name="rebaseRoot"/> (probing the folder itself and each
        /// of its immediate subfolders). This automatically discovers the correct "old root" and
        /// the correct local target folder, which is what makes the multi-VCS-root TeamCity layout
        /// work: several binlogs, each of whose sources were checked out into a different sibling
        /// subfolder (e.g. <c>rebaseRoot\rdws-core-api</c>, <c>rebaseRoot\rdws-model</c>, ...), can
        /// all be rebased with a single <c>/rebase:&lt;parent&gt;</c> value.
        /// </para>
        /// When probing finds nothing (e.g. the sources aren't present), it falls back to a
        /// best-effort longest-common-prefix rewrite onto <paramref name="rebaseRoot"/>.
        /// </summary>
        private static GenerateFromBuildLog.CompilerInvocation[] RebaseInvocations(
            IEnumerable<GenerateFromBuildLog.CompilerInvocation> invocations,
            string rebaseRoot)
        {
            var list = invocations.ToArray();

            // Local directories a recorded project could have been relocated into. Supports both:
            //  * rebaseRoot IS the repository checkout (single VCS root), and
            //  * rebaseRoot CONTAINS several sibling checkouts, one per VCS root (TeamCity with
            //    multiple VCS roots, each mapped to its own checkout subfolder).
            var candidateBases = GetCandidateBaseDirectories(rebaseRoot);

            // Only used when filesystem probing can't locate an invocation's sources.
            var projectPaths = list
                .Select(inv => inv.ProjectFilePath)
                .Where(p => !string.IsNullOrEmpty(p) && p != "-")
                .ToArray();
            var fallbackOldRoot = FindCommonPathPrefix(projectPaths);

            // Probing the disk is relatively expensive, so remember the resolved mapping per
            // distinct project path (all invocations from the same checkout share it).
            var probeCache = new ConcurrentDictionary<string, (string OldRoot, string NewRoot)>(StringComparer.OrdinalIgnoreCase);

            var result = new GenerateFromBuildLog.CompilerInvocation[list.Length];
            Parallel.For(0, list.Length, i =>
            {
                var inv = list[i];

                var (oldRoot, newRoot) = ResolveRebaseMapping(inv.ProjectFilePath, candidateBases, probeCache);

                // Fall back to the old longest-common-prefix behaviour if probing failed.
                if (oldRoot == null && fallbackOldRoot != null)
                {
                    oldRoot = fallbackOldRoot;
                    newRoot = rebaseRoot;
                }

                if (oldRoot == null || string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = inv;
                    return;
                }

                var oldRootRegex = new Regex(Regex.Escape(oldRoot), RegexOptions.IgnoreCase);

                // The command line embeds paths using the separator style of the machine that
                // produced the binlog (e.g. '/' for a Linux build). Only the oldRoot prefix is
                // replaced, so the remainder keeps that style. Emit newRoot in the same style,
                // otherwise we get mixed separators like "D:\...\RdwsSourceBrowser/rdws/..." which
                // Roslyn's metadata reference resolver rejects ("Can't resolve metadata reference").
                var recordedSeparator = oldRoot.IndexOf('\\') >= 0 ? '\\' : '/';
                var newRootForCommandLine = newRoot.Replace('\\', recordedSeparator).Replace('/', recordedSeparator);
                var escapedNewRoot = newRootForCommandLine.Replace("$", "$$"); // '$' is special in Regex replacements

                var rebased = new GenerateFromBuildLog.CompilerInvocation
                {
                    ProjectFilePath      = RebasePath(inv.ProjectFilePath, oldRoot, newRoot),
                    CommandLineArguments = oldRootRegex.Replace(inv.CommandLineArguments ?? string.Empty, escapedNewRoot),
                    SolutionRoot         = inv.SolutionRoot,
                    TypeScriptFiles      = inv.TypeScriptFiles,
                    Language             = inv.Language,
                };

                // Re-derive the output assembly path from the rebased project directory and command
                // line. The value coming from the binlog reader may point at a foreign filesystem
                // (and won't share oldRoot's separator/root style, so a plain prefix rebase can't
                // fix it). Fall back to a prefix rebase of the original value if we can't recompute.
                rebased.OutputAssemblyPath =
                    RecomputeOutputAssemblyPath(rebased)
                    ?? RebasePath(inv.OutputAssemblyPath, oldRoot, newRoot);

                result[i] = rebased;
            });

            return result;
        }

        // Returns rebaseRoot plus its immediate subdirectories. The subdirectories cover the layout
        // where several VCS roots are checked out as siblings under one parent folder.
        private static string[] GetCandidateBaseDirectories(string rebaseRoot)
        {
            var bases = new List<string> { rebaseRoot };
            try
            {
                if (Directory.Exists(rebaseRoot))
                {
                    bases.AddRange(Directory.GetDirectories(rebaseRoot));
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, $"Could not enumerate rebase subdirectories under: {rebaseRoot}", isSevere: false);
            }

            return bases.ToArray();
        }

        // Locates where the recorded project file actually lives locally by probing each candidate
        // base directory for the longest matching path suffix. Returns (oldRoot, newRoot) such that
        // replacing oldRoot with newRoot at the start of any path recorded by the same build yields
        // its correct local location, or (null, null) if the project could not be found.
        private static (string OldRoot, string NewRoot) ResolveRebaseMapping(
            string projectFilePath,
            string[] candidateBases,
            ConcurrentDictionary<string, (string OldRoot, string NewRoot)> probeCache)
        {
            if (string.IsNullOrEmpty(projectFilePath) || projectFilePath == "-")
            {
                return (null, null);
            }

            return probeCache.GetOrAdd(projectFilePath, p => ProbeRebaseMapping(p, candidateBases));
        }

        private static (string OldRoot, string NewRoot) ProbeRebaseMapping(string projectFilePath, string[] candidateBases)
        {
            var segments = projectFilePath.Split(PathSeparators);

            string bestOldRoot = null;
            string bestNewRoot = null;
            int bestSuffixSegmentCount = -1;

            foreach (var baseDir in candidateBases)
            {
                // Try the longest suffix first (i == 1). The first suffix that exists under this
                // base is the longest one, so we can stop probing this base immediately.
                for (int i = 1; i < segments.Length; i++)
                {
                    var suffix = string.Join(Path.DirectorySeparatorChar.ToString(), segments.Skip(i));
                    var candidate = Path.Combine(baseDir, suffix);
                    if (File.Exists(candidate))
                    {
                        int suffixSegmentCount = segments.Length - i;
                        if (suffixSegmentCount > bestSuffixSegmentCount)
                        {
                            bestSuffixSegmentCount = suffixSegmentCount;
                            bestNewRoot = baseDir;
                            bestOldRoot = ReconstructPrefix(projectFilePath, i);
                        }

                        break;
                    }
                }
            }

            return (bestOldRoot, bestNewRoot);
        }

        // Returns the prefix of <paramref name="path"/> that precedes its <paramref name="leadingSegments"/>-th
        // separator, i.e. the portion made up of the first <paramref name="leadingSegments"/> segments,
        // with the original separators preserved and no trailing separator.
        private static string ReconstructPrefix(string path, int leadingSegments)
        {
            int sepCount = 0;
            int prefixEnd = path.Length;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '\\' || path[i] == '/')
                {
                    sepCount++;
                    if (sepCount == leadingSegments)
                    {
                        prefixEnd = i;
                        break;
                    }
                }
            }

            return path.Substring(0, prefixEnd);
        }


        // Both '\' and '/' are always treated as separators regardless of the OS this tool
        // runs on. Otherwise a Windows-produced binlog (backslash paths) read on Linux/macOS
        // would never split into segments, the common prefix would collapse, and rebasing would
        // silently no-op.
        private static readonly char[] PathSeparators = { '\\', '/' };

        // Returns the longest common directory-segment prefix shared by all paths, preserving the
        // original separators of the recorded paths so it can be matched back against them.
        // e.g. ["D:\agent1\work\repo\a\foo.cs", "D:\agent1\work\repo\b\bar.cs"]
        //      returns "D:\agent1\work\repo".
        private static string FindCommonPathPrefix(string[] paths)
        {
            if (paths.Length == 0) { return null; }

            var splitPaths = paths
                .Select(p => p.Split(PathSeparators))
                .ToArray();

            var first = splitPaths[0];
            int commonLength = first.Length;

            foreach (var parts in splitPaths.Skip(1))
            {
                int maxComparable = Math.Min(commonLength, parts.Length);
                int match = 0;
                while (match < maxComparable && string.Equals(first[match], parts[match], StringComparison.OrdinalIgnoreCase))
                {
                    match++;
                }
                commonLength = match;
                if (commonLength == 0) { return null; }
            }

            // Drop the last segment if it looks like a file (has an extension).
            // We want a directory, not a file path.
            if (commonLength > 0 && Path.HasExtension(first[commonLength - 1]))
            {
                commonLength--;
            }

            if (commonLength == 0) { return null; }

            // Reconstruct the prefix from the original first path so its separators (which may be
            // '\' on a foreign-OS binlog) are preserved exactly for later prefix matching.
            var firstPath = paths[0];
            int sepCount = 0;
            int prefixEnd = firstPath.Length;
            for (int i = 0; i < firstPath.Length; i++)
            {
                if (firstPath[i] == '\\' || firstPath[i] == '/')
                {
                    sepCount++;
                    if (sepCount == commonLength)
                    {
                        prefixEnd = i;
                        break;
                    }
                }
            }

            return firstPath.Substring(0, prefixEnd);
        }

        private static string RebasePath(string path, string oldRoot, string newRoot)
        {
            if (string.IsNullOrEmpty(path)) { return path; }
            if (path.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Normalise the remaining separators to the local OS so the rebased path resolves
                // on this machine (e.g. a Windows binlog's '\' segments become '/' on Linux).
                var remainder = path.Substring(oldRoot.Length)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                return newRoot + remainder;
            }
            return path;
        }

        // Re-parses the (already rebased) command line to determine where the output assembly would
        // live on the local machine. Returns null when it can't be determined (e.g. TypeScript
        // invocations, response files that only exist on the build agent, or parse failures), in
        // which case the caller falls back to a best-effort prefix rebase of the recorded value.
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
                Log.Exception(ex, $"Could not recompute output assembly path after rebasing: {invocation.ProjectFilePath}", isSevere: false);
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
            string binlogRebasePath = null)
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
                        var invocations = BinLogCompilerInvocationsReader.ExtractInvocations(path).ToArray();
                        if (binlogRebasePath != null)
                        {
                            invocations = RebaseInvocations(invocations, binlogRebasePath);
                        }

                        // Diagnostic: for cross-binlog debugging we want to be able to see exactly
                        // which invocations each binlog contributed and their assembly names, so a
                        // "missing" project can be traced back to the reader vs the indexer.
                        Log.Message(string.Format(
                            "Binlog '{0}' produced {1} invocation(s): {2}",
                            path,
                            invocations.Length,
                            string.Join(", ", invocations.Select(inv =>
                                (inv.AssemblyName ?? "<no-assembly>") + " (" + (inv.ProjectFilePath ?? "-") + ")"))));

                        // Record each project's output DLL so a reference to it from a sibling
                        // project can be redirected to the local (bin/) copy when the path the
                        // build recorded isn't present locally. This lets Roslyn bind cross-project
                        // symbols so cross-assembly references are captured.
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
