using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace ImportActivityViewer.Editor
{
    internal sealed class CascadeNode
    {
        public AssetImportRecord Record;
        public AssetImportRecord CausedBy;
        public readonly List<CascadeNode> Children = new List<CascadeNode>();
    }

    internal sealed class CascadeGroup
    {
        public AssetImportRecord Root;
        public CascadeNode Tree;
        public readonly List<AssetImportRecord> AllAssets = new List<AssetImportRecord>();

        // Computed once in CascadeAnalyzer.Analyze rather than recomputed on every list bind/sort.
        public int AffectedCount;
        public double TotalImportMs;
        public string Reason;
        public System.DateTime? LastImportedAt;

        public void ComputeSummary()
        {
            AffectedCount = AllAssets.Count - 1;
            TotalImportMs = AllAssets.Sum(a => a.ImportDurationMs);
            Reason = AffectedCount > 0
                ? $"Triggered reimport of {AffectedCount} dependent asset{(AffectedCount == 1 ? "" : "s")}"
                : "Imported directly (no dependents reimported)";
            LastImportedAt = Root.ImportedAt;
        }
    }

    // Determines which assets triggered a reimport cascade and which were merely dragged along.
    //
    // Primary signal: Unity's own ArtifactDifferenceReporter output (AssetImportRecord.CausedByPath),
    // populated in ImportActivityReflection by diffing an asset's current revision against its
    // previous one -- the exact same text the built-in Import Activity window shows for "reason for
    // reimport". This gives a deterministic cause -> effect edge per asset.
    //
    // Fallback: for assets where no previous revision was cached (so no diff was possible), we fall
    // back to matching the raw dependency GUID set against other assets that were also reimported.
    // This is a weaker signal (it doesn't prove the dependency's hash actually changed), so it only
    // applies when the primary signal is unavailable.
    internal static class CascadeAnalyzer
    {
        static readonly Regex k_GuidPattern = new Regex("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

        // Unity can cache more than one ArtifactInfo for the same asset path within a session
        // (e.g. different import-context variants of the same GUID). Left undeduplicated, one
        // entry could end up correctly nested as a child under its real cause while the other
        // -- whose own CausedByPath happened not to resolve -- fell through as a second, spurious
        // "root" for the very same asset. Collapsing to one node per path before graph-building
        // fixes that.
        static List<AssetImportRecord> Deduplicate(List<AssetImportRecord> records)
        {
            var byPath = new Dictionary<string, AssetImportRecord>();

            foreach (AssetImportRecord rec in records)
            {
                if (string.IsNullOrEmpty(rec.AssetPath))
                    continue;

                if (!byPath.TryGetValue(rec.AssetPath, out AssetImportRecord existing))
                {
                    byPath[rec.AssetPath] = rec;
                    continue;
                }

                // Keep whichever entry represents the more recent import; fold in anything the
                // discarded duplicate knew that the kept one doesn't.
                bool recIsNewer = rec.ImportedAt.HasValue && (!existing.ImportedAt.HasValue || rec.ImportedAt > existing.ImportedAt);
                AssetImportRecord keep = recIsNewer ? rec : existing;
                AssetImportRecord drop = recIsNewer ? existing : rec;

                if (string.IsNullOrEmpty(keep.CausedByPath) && !string.IsNullOrEmpty(drop.CausedByPath))
                    keep.CausedByPath = drop.CausedByPath;
                foreach (string msg in drop.ReasonMessages)
                    if (!keep.ReasonMessages.Contains(msg))
                        keep.ReasonMessages.Add(msg);

                byPath[rec.AssetPath] = keep;
            }

            return new List<AssetImportRecord>(byPath.Values);
        }

        public static List<CascadeGroup> Analyze(List<AssetImportRecord> records)
        {
            var groups = new List<CascadeGroup>();
            if (records == null || records.Count == 0)
                return groups;

            records = Deduplicate(records);

            var byKey = new Dictionary<string, AssetImportRecord>();
            foreach (AssetImportRecord r in records)
                if (!string.IsNullOrEmpty(r.ArtifactKey) && !byKey.ContainsKey(r.ArtifactKey))
                    byKey[r.ArtifactKey] = r;

            var byPath = records
                .Where(r => !string.IsNullOrEmpty(r.AssetPath))
                .GroupBy(r => r.AssetPath)
                .ToDictionary(g => g.Key, g => g.First());

            AssetImportRecord Resolve(string keyOrPath)
            {
                if (string.IsNullOrEmpty(keyOrPath))
                    return null;
                if (byPath.TryGetValue(keyOrPath, out AssetImportRecord byPathRec))
                    return byPathRec;
                if (byKey.TryGetValue(keyOrPath, out AssetImportRecord byKeyRec))
                    return byKeyRec;
                if (k_GuidPattern.IsMatch(keyOrPath))
                {
                    string path = AssetDatabase.GUIDToAssetPath(keyOrPath);
                    if (!string.IsNullOrEmpty(path) && byPath.TryGetValue(path, out AssetImportRecord viaGuid))
                        return viaGuid;
                }
                return null;
            }

            var causedBy = new Dictionary<AssetImportRecord, AssetImportRecord>();

            // Primary: deterministic reason text from Unity's own diff engine.
            foreach (AssetImportRecord rec in records)
            {
                if (string.IsNullOrEmpty(rec.CausedByPath))
                    continue;
                AssetImportRecord cause = Resolve(rec.CausedByPath);
                if (cause != null && cause != rec)
                    causedBy[rec] = cause;
            }

            // Fallback: structural dependency-graph matching for assets the primary signal couldn't explain.
            foreach (AssetImportRecord rec in records)
            {
                if (causedBy.ContainsKey(rec))
                    continue;

                foreach (DependencyRef dep in rec.Dependencies)
                {
                    AssetImportRecord cause = Resolve(dep.Key);
                    if (cause == null || cause == rec)
                        continue;
                    causedBy[rec] = cause;
                    break;
                }
            }

            var children = new Dictionary<AssetImportRecord, List<AssetImportRecord>>();
            foreach (KeyValuePair<AssetImportRecord, AssetImportRecord> edge in causedBy)
            {
                if (!children.TryGetValue(edge.Value, out List<AssetImportRecord> list))
                    children[edge.Value] = list = new List<AssetImportRecord>();
                list.Add(edge.Key);
            }

            foreach (AssetImportRecord root in records.Where(r => !causedBy.ContainsKey(r)))
            {
                var group = new CascadeGroup { Root = root };
                var visited = new HashSet<AssetImportRecord> { root };
                var rootNode = new CascadeNode { Record = root };
                group.AllAssets.Add(root);

                // Iterative on purpose: a recursive walk here has no depth limit, and a long
                // enough transitive dependency chain in a huge log can recurse deep enough to
                // blow the stack. A StackOverflowException can't be caught in .NET -- it kills
                // the whole editor process outright, which matched a crash appearing partway
                // through a long-running analysis rather than at a predictable point.
                var stack = new Stack<(AssetImportRecord rec, CascadeNode node)>();
                stack.Push((root, rootNode));
                while (stack.Count > 0)
                {
                    (AssetImportRecord rec, CascadeNode node) = stack.Pop();
                    if (!children.TryGetValue(rec, out List<AssetImportRecord> kids))
                        continue;

                    foreach (AssetImportRecord kid in kids)
                    {
                        if (!visited.Add(kid)) // also guards against cycles in the fallback edges
                            continue;
                        var kidNode = new CascadeNode { Record = kid, CausedBy = rec };
                        node.Children.Add(kidNode);
                        group.AllAssets.Add(kid);
                        stack.Push((kid, kidNode));
                    }
                }

                group.Tree = rootNode;
                group.ComputeSummary();
                groups.Add(group);
            }

            return groups.OrderByDescending(g => g.AffectedCount).ThenByDescending(g => g.TotalImportMs).ToList();
        }
    }
}
