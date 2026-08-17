using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public partial class ProjectFinalizer
    {
        private void BackpatchUnreferencedDeclarations(string referencesFolder)
        {
            string declarationMapFile = Path.Combine(ProjectDestinationFolder, Constants.DeclarationMap + ".txt");
            if (!File.Exists(declarationMapFile))
            {
                return;
            }

            Log.Write("Backpatching unreferenced declarations in " + this.AssemblyId);

            var symbolIDToListOfLocationsMap = ReadSymbolIDToListOfLocationsMap(declarationMapFile);

            ProjectGenerator.GenerateRedirectFile(
                this.SolutionFinalizer.SolutionDestinationFolder,
                this.ProjectDestinationFolder,
                symbolIDToListOfLocationsMap.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Select(t => t.Item1.Replace('\\', '/'))));

            var locationsToPatch = new Dictionary<string, List<long>>();
            GetLocationsToPatch(referencesFolder, locationsToPatch, symbolIDToListOfLocationsMap);
            Patch(locationsToPatch);
        }

        private void GetLocationsToPatch(string referencesFolder, Dictionary<string, List<long>> locationsToPatch, Dictionary<string, List<Tuple<string, long>>> symbolIDToListOfLocationsMap)
        {
            foreach (var kvp in symbolIDToListOfLocationsMap)
            {
                var symbolId = kvp.Key;
                var referencesFileForSymbol = Path.Combine(referencesFolder, symbolId + ".txt");
                bool hasReferencesFile = File.Exists(referencesFileForSymbol);

                // This is the final gate: if no raw {symbolId}.txt ever landed in this project's
                // _references folder (e.g. because AddReference in Pass1 computed a different
                // symbolId for the caller than for the declaration - see SymbolIdService.GetId /
                // DocumentationCommentId mismatch), the declaration's hyperlink gets zeroed out here
                // and the declaration page will show "no usages" even if references were detected and
                // written elsewhere under a different symbolId.
                if (!hasReferencesFile)
                {
                    var declaredId = Serialization.HexStringToULong(symbolId);
                    string declaredName = DeclaredSymbols.TryGetValue(declaredId, out var info) ? info.Name : null;
                    if (declaredName != null && Log.IsTracedSymbol(declaredName))
                    {
                        Log.Trace(declaredName, $"GetLocationsToPatch: assemblyId={this.AssemblyId} symbolId={symbolId} " +
                            $"has NO raw references file at '{referencesFileForSymbol}' -> {kvp.Value.Count} declaration location(s) " +
                            "will be BACKPATCHED to zero-id (link removed, 'no usages' will be shown), even if references " +
                            "were recorded elsewhere under a mismatched symbolId.");
                    }

                    foreach (var location in kvp.Value)
                    {
                        if (location.Item2 != 0)
                        {
                            var filePath = Path.Combine(ProjectDestinationFolder, location.Item1 + ".html");
                            AddLocationToPatch(locationsToPatch, filePath, location.Item2);
                        }
                    }
                }
                else
                {
                    var declaredId = Serialization.HexStringToULong(symbolId);
                    if (DeclaredSymbols.TryGetValue(declaredId, out var info) && Log.IsTracedSymbol(info.Name))
                    {
                        Log.Trace(info.Name, $"GetLocationsToPatch: assemblyId={this.AssemblyId} symbolId={symbolId} " +
                            $"HAS a raw references file at '{referencesFileForSymbol}' -> link will be preserved.");
                    }
                }
            }
        }

        private static void Patch(Dictionary<string, List<long>> locationsToPatch)
        {
            byte[] zeroId = SymbolIdService.ZeroId;
            int zeroIdLength = zeroId.Length;
            Parallel.ForEach(locationsToPatch,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                kvp =>
                {
                    kvp.Value.Sort();

                    using (var stream = new FileStream(kvp.Key, FileMode.Open, FileAccess.ReadWrite))
                    {
                        foreach (var offset in kvp.Value)
                        {
                            stream.Seek(offset, SeekOrigin.Begin);
                            stream.Write(zeroId, 0, zeroIdLength);
                        }
                    }
                });
        }

        private Dictionary<string, List<Tuple<string, long>>> ReadSymbolIDToListOfLocationsMap(string declarationMapFile)
        {
            var result = new Dictionary<string, List<Tuple<string, long>>>();

            var lines = File.ReadAllLines(declarationMapFile);

            //File.Delete(declarationMapFile);

            List<Tuple<string, long>> bucket = null;
            string symbolId = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("=", StringComparison.Ordinal))
                {
                    symbolId = line.Substring(1);
                    bucket = new List<Tuple<string, long>>();
                    result.Add(symbolId, bucket);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    var parts = line.Split(';');
                    var streamOffset = long.Parse(parts[1]);
                    var tuple = Tuple.Create(parts[0], streamOffset);
                    bucket.Add(tuple);
                }
            }

            return result;
        }

        private void AddLocationToPatch(Dictionary<string, List<long>> locationsToPatch, string filePath, long offset)
        {
            if (!locationsToPatch.TryGetValue(filePath, out List<long> offsets))
            {
                offsets = new List<long>();
                locationsToPatch.Add(filePath, offsets);
            }

            offsets.Add(offset);
        }
    }
}
