using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
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
                + "[/rebase:<localreporoot>] "
                + "Plugins are now off by default.");
        }

        /// <summary>
        /// Rewrites paths in compiler invocations extracted from a .binlog that was produced on
        /// a different machine/agent.  Finds the old machine prefix by locating the repository
        /// folder name inside recorded paths and replaces it with <paramref name="newLocalRoot"/>.
        /// </summary>
        private static GenerateFromBuildLog.CompilerInvocation[] RebaseInvocations(
            IEnumerable<GenerateFromBuildLog.CompilerInvocation> invocations,
            string newLocalRoot)
        {
            var repoName = Path.GetFileName(newLocalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string oldRoot = null;

            var result = new List<GenerateFromBuildLog.CompilerInvocation>();
            foreach (var inv in invocations)
            {
                if (oldRoot == null)
                {
                    oldRoot = FindRepoRoot(inv.ProjectFilePath, repoName)
                           ?? FindRepoRoot(inv.OutputAssemblyPath, repoName);
                }

                if (oldRoot == null || string.Equals(oldRoot, newLocalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(inv);
                    continue;
                }

                result.Add(new GenerateFromBuildLog.CompilerInvocation
                {
                    ProjectFilePath = RebasePath(inv.ProjectFilePath, oldRoot, newLocalRoot),
                    OutputAssemblyPath = RebasePath(inv.OutputAssemblyPath, oldRoot, newLocalRoot),
                    CommandLineArguments = RebaseString(inv.CommandLineArguments, oldRoot, newLocalRoot),
                    SolutionRoot = inv.SolutionRoot,
                    TypeScriptFiles = inv.TypeScriptFiles,
                    Language = inv.Language,
                });
            }

            return result.ToArray();
        }

        // Walk path segments looking for a folder matching repoName (case-insensitive).
        // Returns everything up to and including that folder, e.g. "D:\work\Infra".
        private static string FindRepoRoot(string path, string repoName)
        {
            if (string.IsNullOrEmpty(path)) { return null; }
            var normalised = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var parts = normalised.Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], repoName, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join(Path.DirectorySeparatorChar.ToString(), parts, 0, i + 1);
                }
            }
            return null;
        }

        private static string RebasePath(string path, string oldRoot, string newRoot)
        {
            if (string.IsNullOrEmpty(path)) { return path; }
            if (path.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase))
            {
                return newRoot + path.Substring(oldRoot.Length);
            }
            return path;
        }

        // Case-insensitive string replace for the raw command-line text.
        // Uses Regex because string.Replace(string,string,StringComparison) is .NET Core only.
        private static string RebaseString(string text, string oldRoot, string newRoot)
        {
            if (string.IsNullOrEmpty(text)) { return text; }
            // Escape '$' in replacement to avoid Regex treating it as a back-reference.
            return Regex.Replace(text, Regex.Escape(oldRoot), newRoot.Replace("$", "$$"), RegexOptions.IgnoreCase);
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
                        var solutionExplorerSemaphore = new SemaphoreSlim(1, 1);
                        var invocationSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
                        var invocationTasks = invocations.Select(async invocation =>
                        {
                            await invocationSemaphore.WaitAsync(cancellationToken);
                            try
                            {
                                await GenerateFromBuildLog.GenerateInvocationAsync(
                                    invocation,
                                    cancellationToken,
                                    serverPathMappings,
                                    processedAssemblyList,
                                    assemblyNames,
                                    solutionFolder,
                                    solutionExplorerSemaphore,
                                    includeSourceGeneratedDocuments: includeSourceGeneratedDocuments);
                            }
                            finally
                            {
                                invocationSemaphore.Release();
                            }
                        }).ToArray();
                        await Task.WhenAll(invocationTasks);
                        
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
