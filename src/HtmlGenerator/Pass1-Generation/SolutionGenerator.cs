using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class SolutionGenerator : IDisposable
    {
        public string SolutionSourceFolder { get; private set; }
        public string SolutionDestinationFolder { get; private set; }
        public string ProjectFilePath { get; private set; }
        public IReadOnlyDictionary<string, string> ServerPathMappings { get; set; }
        private Federation Federation { get; set; }
        public bool IncludeSourceGeneratedDocuments { get; }

        public IEnumerable<string> PluginBlacklist { get; private set; }
        private readonly HashSet<string> typeScriptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public MEF.PluginAggregator PluginAggregator;

        /// <summary>
        /// List of all assembly names included in the index, from all solutions
        /// </summary>
        public HashSet<string> GlobalAssemblyList { get; set; }

        private Solution solution;
        private Workspace workspace;

        private SolutionGenerator(
            string solutionFilePath,
            string solutionDestinationFolder,
            ImmutableDictionary<string, string> properties,
            Federation federation,
            IReadOnlyDictionary<string, string> serverPathMappings,
            IEnumerable<string> pluginBlacklist,
            bool includeSourceGeneratedDocuments)
        {
            this.SolutionSourceFolder = Path.GetDirectoryName(solutionFilePath);
            this.SolutionDestinationFolder = solutionDestinationFolder;
            this.ProjectFilePath = solutionFilePath;
            ServerPathMappings = serverPathMappings;
            this.Federation = federation ?? new Federation();
            this.PluginBlacklist = pluginBlacklist ?? Enumerable.Empty<string>();
            this.IncludeSourceGeneratedDocuments = includeSourceGeneratedDocuments;
        }

        public static async Task<SolutionGenerator> CreateAsync(
            string solutionFilePath,
            string solutionDestinationFolder,
            CancellationToken cancellationToken,
            ImmutableDictionary<string, string> properties = null,
            Federation federation = null,
            IReadOnlyDictionary<string, string> serverPathMappings = null,
            IEnumerable<string> pluginBlacklist = null,
            bool doNotIncludeReferencedProjects = false,
            bool includeSourceGeneratedDocuments = true)
        {
            var solutionGenerator = new SolutionGenerator(
                solutionFilePath,
                solutionDestinationFolder,
                properties,
                federation,
                serverPathMappings,
                pluginBlacklist,
                includeSourceGeneratedDocuments
            );
            solutionGenerator.solution = await solutionGenerator.CreateSolutionAsync(solutionFilePath, cancellationToken, properties, doNotIncludeReferencedProjects);

            if (LoadPlugins)
            {
                solutionGenerator.SetupPluginAggregator();
            }

            return solutionGenerator;
        }

        public static bool LoadPlugins { get; set; }
        public static bool ExcludeTests { get; set; }

        /// <summary>
        /// When multiple projects share the same assembly short name, index the duplicates under a
        /// unique folder name (e.g. Foo_2) instead of dropping all but the first one.
        /// </summary>
        public static bool AllowDuplicateAssemblies { get; set; }

        private void SetupPluginAggregator()
        {
            if (!LoadPlugins)
            {
                return;
            }

            var settings = System.Configuration.ConfigurationManager.AppSettings;
            var configs = settings
                .AllKeys
                .Where(k => k.Contains(':'))                            //Ignore keys that don't have a colon to indicate which plugin they go to
                .Select(k => Tuple.Create(k.Split(':'), settings[k]))   //Get the data -- split the key to get the plugin name and setting name, look up the key to get the value
                .GroupBy(t => t.Item1[0])                               //Group the settings based on which plugin they're for
                .ToDictionary(
                    group => group.Key,                                 //Index the outer dictionary based on plugin
                    group => group.ToDictionary(
                        t => t.Item1[1],                                //Index the inner dictionary based on setting name
                        t => t.Item2                                    //The actual value of the setting
                    )
                );
            PluginAggregator = new MEF.PluginAggregator(configs, new Utilities.PluginLogger(), PluginBlacklist);
            FirstChanceExceptionHandler.IgnoreModules(PluginAggregator.Select(p => p.PluginModule));
            PluginAggregator.Init();
        }

        public SolutionGenerator(
            string projectFilePath,
            string commandLineArguments,
            string outputAssemblyPath,
            string solutionSourceFolder,
            string solutionDestinationFolder,
            bool includeSourceGeneratedDocuments)
        {
            this.ProjectFilePath = projectFilePath;
            string projectName = Path.GetFileNameWithoutExtension(projectFilePath);
            string language = projectFilePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ?
                LanguageNames.VisualBasic : LanguageNames.CSharp;
            this.SolutionSourceFolder = solutionSourceFolder;
            this.SolutionDestinationFolder = solutionDestinationFolder;
            this.IncludeSourceGeneratedDocuments = includeSourceGeneratedDocuments;
            string projectSourceFolder = Path.GetDirectoryName(projectFilePath);
            SetupPluginAggregator();

            this.solution = CreateSolution(
                commandLineArguments,
                projectName,
                language,
                projectSourceFolder,
                outputAssemblyPath);
        }

        // Constructs a generator around a pre-built, multi-project Roslyn solution (see
        // CreateFromInvocations). Used for the binlog path so that several co-indexed assemblies
        // live in ONE solution and can reference each other by source rather than metadata.
        private SolutionGenerator(
            Solution solution,
            string solutionSourceFolder,
            string solutionDestinationFolder,
            bool includeSourceGeneratedDocuments)
        {
            this.solution = solution;
            this.workspace = solution?.Workspace;
            this.SolutionSourceFolder = solutionSourceFolder;
            this.SolutionDestinationFolder = solutionDestinationFolder;
            this.IncludeSourceGeneratedDocuments = includeSourceGeneratedDocuments;
            SetupPluginAggregator();
        }

        /// <summary>
        /// Builds a single Roslyn solution containing every supplied binlog invocation as its own
        /// project, then rewrites the metadata references that point at co-indexed assemblies into
        /// project (source) references. This makes callers bind to the SAME source symbols the
        /// declaration is generated from, so their DocumentationCommentId (and therefore the
        /// SourceBrowser symbol id) matches and cross-assembly "find all references" works in
        /// binlog mode exactly like it does when indexing a .sln. Without this, each invocation is
        /// compiled in isolation against sibling assemblies' metadata; when a referenced type can't
        /// be resolved locally the signature falls back to an error type, the symbol id diverges,
        /// and the declaration page shows no usages.
        /// </summary>
        public static SolutionGenerator CreateFromInvocations(
            IReadOnlyList<GenerateFromBuildLog.CompilerInvocation> invocations,
            string solutionSourceFolder,
            string solutionDestinationFolder,
            bool includeSourceGeneratedDocuments,
            IReadOnlyDictionary<string, string> serverPathMappings = null)
        {
            var workspace = CreateWorkspace();
            var solution = BuildCombinedSolution(workspace, invocations);

            var generator = new SolutionGenerator(
                solution,
                solutionSourceFolder,
                solutionDestinationFolder,
                includeSourceGeneratedDocuments);
            generator.ServerPathMappings = serverPathMappings;
            return generator;
        }

        private static Solution BuildCombinedSolution(
            Workspace workspace,
            IReadOnlyList<GenerateFromBuildLog.CompilerInvocation> invocations)
        {
            var solution = workspace.CurrentSolution;

            // First pass: add every invocation as a project and remember, per project, its assembly
            // short name, output DLL path, language, and the set of assembly names it references
            // (taken from the ORIGINAL parsed command line so refs that get dropped/redirected below
            // can still be turned into project references).
            var projectIdByAssemblyName = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
            var outputPathByProjectId = new Dictionary<ProjectId, string>();
            var languageByProjectId = new Dictionary<ProjectId, string>();
            var referencedAssemblyNamesByProjectId = new Dictionary<ProjectId, HashSet<string>>();

            foreach (var invocation in invocations)
            {
                var projectFilePath = invocation.ProjectFilePath;
                var projectName = Path.GetFileNameWithoutExtension(projectFilePath);
                var language = ".vbproj".Equals(Path.GetExtension(projectFilePath), StringComparison.OrdinalIgnoreCase)
                    ? LanguageNames.VisualBasic
                    : LanguageNames.CSharp;
                var projectSourceFolder = Path.GetDirectoryName(projectFilePath);
                var commandLineArguments = RemoveNonExistentReferencesFromCommandLine(invocation.CommandLineArguments, projectSourceFolder);

                var projectInfo = CommandLineProject.CreateProjectInfo(
                    projectName,
                    language,
                    commandLineArguments,
                    projectSourceFolder,
                    workspace);
                solution = solution.AddProject(projectInfo);

                projectIdByAssemblyName[invocation.AssemblyName] = projectInfo.Id;
                outputPathByProjectId[projectInfo.Id] = invocation.OutputAssemblyPath;
                languageByProjectId[projectInfo.Id] = language;

                var referencedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in invocation.Parsed.MetadataReferences)
                {
                    referencedNames.Add(Path.GetFileNameWithoutExtension(reference.Reference));
                }
                referencedAssemblyNamesByProjectId[projectInfo.Id] = referencedNames;
            }

            // Second pass: for every reference that targets another co-indexed assembly, add a
            // project reference and drop the matching metadata reference so binding goes to source.
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                foreach (var referencedName in referencedAssemblyNamesByProjectId[projectId])
                {
                    if (!projectIdByAssemblyName.TryGetValue(referencedName, out var targetProjectId) ||
                        targetProjectId == projectId)
                    {
                        continue;
                    }

                    var project = solution.GetProject(projectId);
                    var projectReference = new ProjectReference(targetProjectId);
                    if (!project.AllProjectReferences.Contains(projectReference))
                    {
                        solution = solution.AddProjectReference(projectId, projectReference);
                        Log.Message($"Wired project reference: {project.Name} -> {referencedName} (source, not metadata).");
                    }

                    project = solution.GetProject(projectId);
                    foreach (var metadataReference in project.MetadataReferences.ToArray())
                    {
                        if (string.Equals(Path.GetFileNameWithoutExtension(metadataReference.Display), referencedName, StringComparison.OrdinalIgnoreCase))
                        {
                            solution = solution.GetProject(projectId).RemoveMetadataReference(metadataReference).Solution;
                        }
                    }
                }
            }

            // Post-process the combined solution exactly like the single-project CreateSolution does.
            solution = RemoveNonExistingFiles(solution);
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                solution = AddAssemblyAttributesFile(languageByProjectId[projectId], outputPathByProjectId[projectId], solution, projectId);
            }
            solution = DisambiguateSameNameLinkedFiles(solution);
            solution = DeduplicateProjectReferences(solution);

            solution.Workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, solution.Workspace));

            return solution;
        }

        public IEnumerable<string> GetAssemblyNames()
        {
            if (solution != null)
            {
                return solution.Projects.Select(p => p.AssemblyName);
            }
            else
            {
                return Enumerable.Empty<string>();
            }
        }

        private static MSBuildWorkspace CreateWorkspace(ImmutableDictionary<string, string> propertiesOpt = null)
        {
            propertiesOpt = propertiesOpt ?? ImmutableDictionary<string, string>.Empty;
            propertiesOpt = propertiesOpt.Add("AlwaysCompileMarkupFilesInSeparateDomain", "false");

            var w = MSBuildWorkspace.Create(properties: propertiesOpt);
            w.LoadMetadataForReferencedProjects = true;
            w.AssociateFileExtensionWithLanguage("depproj", LanguageNames.CSharp);
            return w;
        }

        private static Solution CreateSolution(
            string commandLineArguments,
            string projectName,
            string language,
            string projectSourceFolder,
            string outputAssemblyPath)
        {
            var workspace = CreateWorkspace();

            // References/analyzers recorded in the binlog may point at files that don't exist on
            // this machine (e.g. NuGet/SDK assemblies under a Linux build agent's
            // '/opt/buildagent/system/dotnet/.nuget/...' path when indexing on Windows). Roslyn's
            // CommandLineProject.CreateProjectInfo throws ArgumentException ("Can't resolve
            // metadata reference") and aborts the whole project before SourceBrowser's later
            // RemoveNonExistingReferences filter can run, so strip those switches up front.
            commandLineArguments = RemoveNonExistentReferencesFromCommandLine(commandLineArguments, projectSourceFolder);

            var projectInfo = CommandLineProject.CreateProjectInfo(
                projectName,
                language,
                commandLineArguments,
                projectSourceFolder,
                workspace);
            var solution = workspace.CurrentSolution.AddProject(projectInfo);

            solution = RemoveNonExistingFiles(solution);
            solution = AddAssemblyAttributesFile(language, outputAssemblyPath, solution);
            solution = DisambiguateSameNameLinkedFiles(solution);
            solution = DeduplicateProjectReferences(solution);

            solution.Workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, solution.Workspace));

            return solution;
        }

        // Matches the file-resolving compiler switches whose targets Roslyn insists on resolving
        // eagerly (and throws on if missing): references, linked (embed-interop) references and
        // analyzers, in both their long and short forms.
        private static readonly Regex ReferenceSwitchRegex = new Regex(
            @"(?<switch>/(?:reference|r|link|l|analyzer|a):)(?:(?<alias>\w+)=)?(?:""(?<path>[^""]*)""|(?<path>[^""\s]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Maps an assembly short name to an existing local output DLL, built from the binlog
        // invocations' own outputs. Used to redirect a project's /reference: switch when the path
        // the build recorded (typically a sibling project's obj/ output, or a foreign build-agent
        // path) is absent locally but the same assembly's bin/ DLL was supplied. Without this,
        // such references get dropped and Roslyn can't bind cross-project symbols, so cross-
        // assembly "find all references" comes up empty. Deliberately separate from
        // MetadataReading's AssemblyNameToFilePathMap so the metadata-as-source path is unaffected.
        public static readonly ConcurrentDictionary<string, string> LocalReferenceAssemblyMap =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Records a binlog invocation's output assembly (resolving obj->bin when only the bin copy
        // was supplied) so its DLL can later stand in for missing /reference: paths to it.
        public static void RegisterLocalOutputAssembly(string outputAssemblyPath)
        {
            if (string.IsNullOrEmpty(outputAssemblyPath))
            {
                return;
            }

            var resolved = File.Exists(outputAssemblyPath)
                ? outputAssemblyPath
                : TryResolveFromBinFolder(outputAssemblyPath);
            if (resolved != null)
            {
                LocalReferenceAssemblyMap[Path.GetFileNameWithoutExtension(resolved)] = resolved;
            }
            else
            {
                Log.Message("RegisterLocalOutputAssembly: could not locate DLL for " + outputAssemblyPath);
            }
        }

        /// <summary>
        /// Removes reference/link/analyzer switches from a compiler command line when the file they
        /// point at does not exist locally. This lets binlogs produced on another machine (where
        /// NuGet/SDK assemblies live at foreign, non-rebasable paths) be indexed without Roslyn
        /// aborting the project during <see cref="CommandLineProject.CreateProjectInfo"/>.
        /// </summary>
        private static string RemoveNonExistentReferencesFromCommandLine(string commandLineArguments, string projectSourceFolder)
        {
            if (string.IsNullOrEmpty(commandLineArguments))
            {
                return commandLineArguments;
            }

            return ReferenceSwitchRegex.Replace(commandLineArguments, match =>
            {
                // A single switch may list several comma-separated paths.
                var paths = match.Groups["path"].Value.Split(',');

                // Fast path: everything the build recorded is present locally, leave it untouched.
                if (paths.All(p => ReferenceFileExists(p, projectSourceFolder)))
                {
                    return match.Value;
                }

                // Otherwise keep paths that exist, redirect missing ones to the local bin/ DLL of
                // the same assembly (sibling projects in a single binlog reference each other's
                // unshipped obj/ outputs), and drop only those we genuinely can't locate.
                var resolved = paths
                    .Select(p =>
                    {
                        var r = ResolveReferencePath(p, projectSourceFolder);
                        if (r == null)
                        {
                            Log.Message("DROPPED reference (not found locally, no substitute): " + p);
                        }
                        else if (!string.Equals(r, p, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Message("REDIRECTED reference: " + p + " -> " + r);
                        }
                        return r;
                    })
                    .Where(p => p != null)
                    .ToArray();

                if (resolved.Length == 0)
                {
                    return string.Empty;
                }

                var alias = match.Groups["alias"].Success ? match.Groups["alias"].Value + "=" : string.Empty;
                var rebuiltPaths = string.Join(",", resolved.Select(p => p.IndexOf(' ') >= 0 ? "\"" + p + "\"" : p));
                return match.Groups["switch"].Value + alias + rebuiltPaths;
            });
        }

        // Returns the local path Roslyn should use for a recorded reference path: the path itself
        // if it exists, otherwise the same assembly's supplied bin/ DLL, or null if neither is
        // available (in which case the reference is dropped).
        private static string ResolveReferencePath(string path, string projectSourceFolder)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var resolved = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(projectSourceFolder ?? string.Empty, path);
                if (File.Exists(resolved))
                {
                    return resolved;
                }
            }
            catch
            {
                // Malformed path (e.g. foreign-OS characters); fall through to the map lookup.
            }

            if (LocalReferenceAssemblyMap.TryGetValue(Path.GetFileNameWithoutExtension(path), out var localPath))
            {
                return localPath;
            }

            return null;
        }

        private static bool ReferenceFileExists(string path, string projectSourceFolder)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var resolved = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(projectSourceFolder ?? string.Empty, path);
                return File.Exists(resolved);
            }
            catch
            {
                // Malformed path (e.g. foreign-OS characters). Treat as non-existent so it's dropped.
                return false;
            }
        }

        private static Solution DisambiguateSameNameLinkedFiles(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);
                solution = DisambiguateSameNameLinkedFiles(project);
            }

            return solution;
        }

        /// <summary>
        /// If there are two linked files both outside the project cone, and they have same names,
        /// they will logically appear as the same file in the project root. To disambiguate, we
        /// remove both files from the project's root and re-add them each into a folder chain that
        /// is formed from the full path of each document.
        /// </summary>
        private static Solution DisambiguateSameNameLinkedFiles(Project project)
        {
            var nameMap = project.Documents.Where(d => !d.Folders.Any()).ToLookup(d => d.Name);
            foreach (var conflictedItemGroup in nameMap.Where(g => g.Count() > 1))
            {
                foreach (var conflictedDocument in conflictedItemGroup)
                {
                    project = project.RemoveDocument(conflictedDocument.Id);
                    string filePath = conflictedDocument.FilePath;
                    DocumentId newId = DocumentId.CreateNewId(project.Id, filePath);
                    var folders = filePath.Split('\\').Select(p => p.TrimEnd(':'));
                    project = project.Solution.AddDocument(
                        newId,
                        conflictedDocument.Name,
                        conflictedDocument.GetTextAsync().Result,
                        folders,
                        filePath).GetProject(project.Id);
                }
            }

            return project.Solution;
        }

        private static Solution RemoveNonExistingFiles(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);
                solution = RemoveNonExistingDocuments(project);

                project = solution.GetProject(projectId);
                solution = RemoveNonExistingReferences(project);
            }

            return solution;
        }

        private static Solution RemoveNonExistingDocuments(Project project)
        {
            foreach (var documentId in project.DocumentIds.ToArray())
            {
                var document = project.GetDocument(documentId);
                if (!File.Exists(document.FilePath))
                {
                    Log.Message("Document doesn't exist on disk: " + document.FilePath);
                    project = project.RemoveDocument(documentId);
                }
            }

            return project.Solution;
        }

        private static Solution RemoveNonExistingReferences(Project project)
        {
            foreach (var metadataReference in project.MetadataReferences.ToArray())
            {
                if (!File.Exists(metadataReference.Display))
                {
                    Log.Message("Reference assembly doesn't exist on disk: " + metadataReference.Display);
                    project = project.RemoveMetadataReference(metadataReference);
                }
            }

            return project.Solution;
        }

        private static Solution AddAssemblyAttributesFile(string language, string outputAssemblyPath, Solution solution)
        {
            return AddAssemblyAttributesFile(language, outputAssemblyPath, solution, solution.Projects.First().Id);
        }

        private static Solution AddAssemblyAttributesFile(string language, string outputAssemblyPath, Solution solution, ProjectId projectId)
        {
            if (!File.Exists(outputAssemblyPath))
            {
                // Builds that only collect the bin/ folder don't ship the obj/ copy the binlog
                // recorded, so look for the same assembly under bin/<tfm>/ before giving up.
                var binFallback = TryResolveFromBinFolder(outputAssemblyPath);
                if (binFallback == null)
                {
                    Log.Exception("AddAssemblyAttributesFile: assembly doesn't exist: " + outputAssemblyPath);
                    return solution;
                }

                outputAssemblyPath = binFallback;
            }

            var assemblyAttributesFileText = MetadataReading.GetAssemblyAttributesFileText(
                assemblyFilePath: outputAssemblyPath,
                language: language);
            if (assemblyAttributesFileText != null)
            {
                var extension = language == LanguageNames.CSharp ? ".cs" : ".vb";
                var newAssemblyAttributesDocumentName = MetadataAsSource.GeneratedAssemblyAttributesFileName + extension;
                var existingAssemblyAttributesFileName = "AssemblyAttributes" + extension;

                var project = solution.GetProject(projectId);
                if (project.Documents.All(d => d.Name != existingAssemblyAttributesFileName || d.Folders.Count != 0))
                {
                    var document = project.AddDocument(
                        newAssemblyAttributesDocumentName,
                        assemblyAttributesFileText,
                        filePath: newAssemblyAttributesDocumentName);
                    solution = document.Project.Solution;
                }
            }

            return solution;
        }
        // Caches the discovered bin folder per obj tree so the path work runs once instead of for
        // every assembly. Keyed by the path segment before "obj", value is the sibling "bin" folder.
        private static readonly ConcurrentDictionary<string, string> binFolderCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string TryResolveFromBinFolder(string outputAssemblyPath)
        {
            if (string.IsNullOrEmpty(outputAssemblyPath))
            {
                return null;
            }

            var fileName = Path.GetFileName(outputAssemblyPath);
            var binFolder = GetBinFolder(Path.GetDirectoryName(outputAssemblyPath));
            if (binFolder == null || !Directory.Exists(binFolder))
            {
                return null;
            }

            // Search recursively so any bin/<platform>/<config>/<tfm>/... layout is covered.
            return Directory.EnumerateFiles(binFolder, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }

        private static string GetBinFolder(string frameworkFolder)
        {
            if (frameworkFolder == null)
            {
                return null;
            }

            // The bin/ copy sits next to obj/, so cut the path at the "obj" folder and swap in "bin".
            var objSegment = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
            var index = frameworkFolder.IndexOf(objSegment, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var repoRoot = frameworkFolder.Substring(0, index);
            return binFolderCache.GetOrAdd(repoRoot, root => Path.Combine(root, "bin"));
        }

        private static Solution DeduplicateProjectReferences(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);

                var distinctProjectReferences = project.AllProjectReferences.Distinct().ToArray();
                if (distinctProjectReferences.Length < project.AllProjectReferences.Count)
                {
                    var duplicates = project.AllProjectReferences.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                    foreach (var duplicate in duplicates)
                    {
                        Log.Write($"Duplicate project reference to {duplicate.ProjectId.ToString()} in project: {project.Name}", ConsoleColor.Yellow);
                    }

                    var newProject = project.WithProjectReferences(distinctProjectReferences);
                    solution = newProject.Solution;
                }
            }

            return solution;
        }

        public static string CurrentAssemblyName = null;

        /// <returns>true if only part of the solution was processed and the method needs to be called again, false if all done</returns>
        public async Task<bool> GenerateAsync(CancellationToken cancellationToken, HashSet<string> processedAssemblyList = null, Folder<ProjectSkeleton> solutionExplorerRoot = null)
        {
            if (solution == null)
            {
                // we failed to open the solution earlier; just return
                Log.Message("Solution is null: " + this.ProjectFilePath);
                return false;
            }

            var allProjects = solution.Projects.ToArray();
            if (allProjects.Length == 0)
            {
                Log.Exception("Solution " + this.ProjectFilePath + " has 0 projects - this is suspicious");
            }

            // Diagnostic: report every project name/assembly Roslyn actually loaded so a
            // duplicate-assembly-name project can't disappear silently between binlog read and
            // indexer.
            Log.Message(string.Format(
                "SolutionGenerator '{0}' loaded {1} project(s): {2}",
                this.ProjectFilePath,
                allProjects.Length,
                string.Join(", ", allProjects.Select(p => (p.AssemblyName ?? "<none>") + " [" + (p.FilePath ?? "-") + "]"))));

            var projectsToProcess = allProjects
                .Where(p => !ExcludeTests || !IsTestProject(p))
                .ToArray();

            // Maps a project to the folder/assembly name it should be indexed under. Only populated
            // for duplicates when AllowDuplicateAssemblies is set; the default (non-duplicate) name
            // is used otherwise.
            var assemblyNameOverrides = new Dictionary<ProjectId, string>();

            var currentBatch = new List<Project>();
            foreach (var project in projectsToProcess)
            {
                if (processedAssemblyList == null)
                {
                    // No cross-invocation tracking (single project / metadata-as-source path): index as-is.
                    currentBatch.Add(project);
                    continue;
                }

                var resolvedName = AssemblyNameDeduplicator.Resolve(
                    project.AssemblyName,
                    processedAssemblyList,
                    AllowDuplicateAssemblies);

                if (resolvedName == null)
                {
                    // Duplicate assembly name and duplicates are not allowed: skip, but say why so the
                    // dropped project isn't a silent mystery.
                    Log.Message(string.Format(
                        "Skipping project '{0}': assembly '{1}' was already indexed. Pass /allowduplicateassemblies to index it under a separate folder.",
                        project.FilePath,
                        project.AssemblyName));
                    continue;
                }

                if (resolvedName != project.AssemblyName)
                {
                    // Duplicate that we're keeping: relocate it to a unique folder.
                    assemblyNameOverrides[project.Id] = resolvedName;
                    Log.Message(string.Format(
                        "Assembly '{0}' was already indexed; indexing duplicate project '{1}' as '{2}'.",
                        project.AssemblyName,
                        project.FilePath,
                        resolvedName));
                }

                currentBatch.Add(project);
            }

            foreach (var project in currentBatch)
            {
                try
                {
                    assemblyNameOverrides.TryGetValue(project.Id, out var assemblyNameOverride);
                    var effectiveAssemblyName = assemblyNameOverride ?? project.AssemblyName;
                    CurrentAssemblyName = effectiveAssemblyName;

                    var generator = new ProjectGenerator(this, project, assemblyNameOverride);
                    await generator.GenerateAsync();

                    File.AppendAllText(Paths.ProcessedAssemblies, effectiveAssemblyName + Environment.NewLine, Encoding.UTF8);
                }
                finally
                {
                    CurrentAssemblyName = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }

            new TypeScriptSupport().Generate(typeScriptFiles, SolutionDestinationFolder);

            await AddProjectsToSolutionExplorerAsync(
                solutionExplorerRoot,
                currentBatch,
                assemblyNameOverrides,
                cancellationToken);

            return currentBatch.Count < projectsToProcess.Length;
        }

        private static bool IsTestProject(Project proj)
        {
            return
                proj.MetadataReferences.Any(mdr =>
                {
                    var peRef = mdr as PortableExecutableReference;
                    return
                        IsTestProject(peRef, "xunit.core.dll") ||
                        IsTestProject(peRef, "nunit.framework.dll") ||
                        IsTestProject(peRef, "Microsoft.VisualStudio.TestPlatform.TestFramework.dll");
                }) ||
                IsTestProject(proj, "xunit") ||
                IsTestProject(proj, "nunit") ||
                IsTestProject(proj, "MSTest.TestFramework");
        }

        private static IEnumerable<string> GetPackageRefs(Project proj)
        {
            var projRoot = XElement.Load(proj.FilePath);
            var packageRefs = projRoot.Elements()
                .Where(elem => elem.Name.LocalName == "ItemGroup")
                .SelectMany(elem => elem.Elements())
                .Where(elem => elem.Name.LocalName == "PackageReference")
                .Select(elem => (string)elem.Attribute("Include"));
            return packageRefs;
        }

        private static bool IsTestProject(Project proj, string marker)
        {
            return GetPackageRefs(proj).Any(pr => string.Equals(pr, marker, StringComparison.InvariantCultureIgnoreCase));
        }

        private static bool IsTestProject(PortableExecutableReference peRef, string marker)
        {
            return peRef?.FilePath.EndsWith(marker, StringComparison.InvariantCultureIgnoreCase) ?? false;
        }

        private void SetFieldValue(object instance, string fieldName, object value)
        {
            var type = instance.GetType();
            var fieldInfo = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            fieldInfo.SetValue(instance, null);
        }

        public async Task GenerateExternalReferencesAsync(HashSet<string> assemblyList, CancellationToken cancellationToken)
        {
            var externalReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in solution.Projects)
            {
                var references = project.MetadataReferences
                    .OfType<PortableExecutableReference>()
                    .Where(m => File.Exists(m.FilePath) &&
                                !assemblyList.Contains(Path.GetFileNameWithoutExtension(m.FilePath)) &&
                                !IsPartOfSolution(Path.GetFileNameWithoutExtension(m.FilePath)) &&
                                GetExternalAssemblyIndex(Path.GetFileNameWithoutExtension(m.FilePath)) == -1
                    )
                    .Select(m => Path.GetFullPath(m.FilePath));
                foreach (var reference in references)
                {
                    externalReferences[Path.GetFileNameWithoutExtension(reference)] = reference;
                }
            }

            foreach (var externalReference in externalReferences)
            {
                Log.Write(externalReference.Key, ConsoleColor.Magenta);
                var solutionGenerator = await SolutionGenerator.CreateAsync(
                    externalReference.Value,
                    Paths.SolutionDestinationFolder,
                    cancellationToken,
                    pluginBlacklist: PluginBlacklist);
                await solutionGenerator.GenerateAsync(cancellationToken, assemblyList);
            }
        }

        public bool IsPartOfSolution(string assemblyName)
        {
            if (GlobalAssemblyList == null)
            {
                // if for some reason we don't know a global list, assume everything is in the solution
                // this is better than the alternative
                return true;
            }

            return GlobalAssemblyList.Contains(assemblyName);
        }

        public int GetExternalAssemblyIndex(string assemblyName)
        {
            if (Federation == null)
            {
                return -1;
            }

            return Federation.GetExternalAssemblyIndex(assemblyName);
        }

        private async Task<Solution> CreateSolutionAsync(string solutionFilePath, CancellationToken cancellationToken, ImmutableDictionary<string, string> properties = null, bool doNotIncludeReferencedProjects = false)
        {
            try
            {
                Solution solution = null;
                if (solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    properties = AddSolutionProperties(properties, solutionFilePath);
                    var workspace = CreateWorkspace(properties);
                    workspace.SkipUnrecognizedProjects = true;
                    workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, workspace));
                    solution = await workspace.OpenSolutionAsync(solutionFilePath, cancellationToken: cancellationToken);
                    solution = DeduplicateProjectReferences(solution);
                    this.workspace = workspace;
                }
                else if (
                    solutionFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    var workspace = CreateWorkspace(properties);
                    workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, workspace));
                    solution = (await workspace.OpenProjectAsync(solutionFilePath, cancellationToken: cancellationToken)).Solution;
                    solution = DeduplicateProjectReferences(solution);
                    if (doNotIncludeReferencedProjects)
                    {
                        var keepPrimaryProject = solution.Projects.First(p => string.Equals(p.FilePath, solutionFilePath, StringComparison.OrdinalIgnoreCase));
                        foreach (var projectIdToRemove in solution.ProjectIds.Where(id => id != keepPrimaryProject.Id).ToArray())
                        {
                            solution = solution.RemoveProject(projectIdToRemove);
                        }
                    }

                    this.workspace = workspace;
                }
                else if (
                    solutionFilePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".netmodule", StringComparison.OrdinalIgnoreCase))
                {
                    solution = await MetadataAsSource.LoadMetadataAsSourceSolutionAsync(solutionFilePath, cancellationToken);
                    if (solution != null)
                    {
                        solution.Workspace.RegisterWorkspaceFailedHandler(args => WorkspaceFailed(args, solution.Workspace));
                        workspace = solution.Workspace;
                    }
                }

                return solution;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Failed to open solution: " + solutionFilePath);
                return null;
            }
        }

        private ImmutableDictionary<string, string> AddSolutionProperties(ImmutableDictionary<string, string> properties, string solutionFilePath)
        {
            // http://referencesource.microsoft.com/#MSBuildFiles/C/ProgramFiles(x86)/MSBuild/14.0/bin_/amd64/Microsoft.Common.CurrentVersion.targets,296
            properties = properties ?? ImmutableDictionary<string, string>.Empty;
            properties = properties.Add("SolutionName", Path.GetFileNameWithoutExtension(solutionFilePath));
            properties = properties.Add("SolutionFileName", Path.GetFileName(solutionFilePath));
            properties = properties.Add("SolutionPath", solutionFilePath);
            properties = properties.Add("SolutionDir", Path.GetDirectoryName(solutionFilePath));
            properties = properties.Add("SolutionExt", Path.GetExtension(solutionFilePath));
            return properties;
        }

        private static void WorkspaceFailed(WorkspaceDiagnosticEventArgs e, Workspace workspace)
        {
            var message = e.Diagnostic.Message;
            if (message.StartsWith("Could not find file", StringComparison.Ordinal) || message.StartsWith("Could not find a part of the path", StringComparison.Ordinal))
            {
                return;
            }

            if (message.StartsWith("The imported project ", StringComparison.Ordinal))
            {
                return;
            }

            if (message.Contains("because the file extension '.shproj'"))
            {
                return;
            }

            var project = workspace.CurrentSolution.Projects.FirstOrDefault();
            if (project != null)
            {
                message = message + " Project: " + project.Name;
            }

            Log.Exception("Workspace failed: " + message);
            Log.Write(message, ConsoleColor.Red);
        }

        public void AddTypeScriptFile(string filePath)
        {
            this.typeScriptFiles.Add(filePath);
        }

        public void Dispose()
        {
            if (workspace != null)
            {
                workspace.Dispose();
                workspace = null;
            }
        }
    }
}
