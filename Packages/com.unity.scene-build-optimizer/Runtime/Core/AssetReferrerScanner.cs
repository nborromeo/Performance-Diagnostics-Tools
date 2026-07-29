using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Profile;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Checks whether an asset is referenced by anything other than a specific scene, so optimizers
    /// can avoid silently duplicating an asset that's shared elsewhere in the project.
    ///
    /// Scoped to scenes reachable from Build Settings and Build Profiles' scene overrides by default —
    /// bounded to what could plausibly end up in a build, rather than scanning the whole project.
    ///
    /// NOTE: BuildProfile's exact scene-list API shape (assumed here to behave like
    /// EditorBuildSettingsScene[]) should be double-checked against the installed Unity version —
    /// this was written from documentation rather than a compiler, and Build Profile's public surface
    /// has changed across 6000.x point releases.
    /// </summary>
    public static class AssetReferrerScanner
    {
        // AssetDatabase.GetDependencies(path, true) walks a scene's whole dependency graph — cheap
        // once, but this scanner runs once per asset checked (once per terrain for the Layer
        // Optimizer, once per tile per block for the Tile Merger), against every candidate scene. A
        // scene's own dependency graph can't change mid-run, so recomputing it fresh for every asset
        // checked was pure waste; cached per candidate scene path instead. Callers doing a fresh
        // Generate() run should call InvalidateCache() first — cached entries would otherwise still
        // reflect whatever the project's dependency graph looked like on a previous run.
        static readonly Dictionary<string, string[]> s_DependencyCache = new Dictionary<string, string[]>();

        /// <summary>Clears the per-candidate-scene dependency cache. Call at the start of a fresh Generate() run.</summary>
        public static void InvalidateCache() => s_DependencyCache.Clear();

        public static bool HasReferrerOutsideScene(string assetPath, params string[] excludeScenePaths) =>
            FindReferrersOutsideScene(assetPath, excludeScenePaths).Count > 0;

        /// <summary>
        /// Every scene/prefab (other than <paramref name="excludeScenePaths"/>) that depends on
        /// <paramref name="assetPath"/>, so callers can report specifically what's referencing a shared
        /// asset.
        /// </summary>
        /// <param name="excludeScenePaths">
        /// Pass both the authoring source scene AND the optimized scene currently being (re)generated.
        /// The optimized scene must be excluded too: by the time this runs, its file on disk has
        /// already been freshly overwritten from the source (pre-optimization, still referencing the
        /// original asset) — without excluding it, it would always show up as a false-positive "outside
        /// referrer" of the very asset it's about to be repointed away from, permanently blocking
        /// regeneration.
        /// </param>
        public static List<string> FindReferrersOutsideScene(string assetPath, params string[] excludeScenePaths)
        {
            var referrers = new List<string>();

            foreach (var candidatePath in GetCandidateScenePaths())
            {
                bool excluded = false;
                foreach (var excludePath in excludeScenePaths)
                {
                    if (string.Equals(candidatePath, excludePath, StringComparison.OrdinalIgnoreCase))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded)
                    continue;

                if (!s_DependencyCache.TryGetValue(candidatePath, out var dependencies))
                {
                    dependencies = AssetDatabase.GetDependencies(candidatePath, true);
                    s_DependencyCache[candidatePath] = dependencies;
                }
                if (Array.IndexOf(dependencies, assetPath) >= 0)
                    referrers.Add(candidatePath);
            }

            return referrers;
        }

        static IEnumerable<string> GetCandidateScenePaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && seen.Add(scene.path))
                    yield return scene.path;
            }

            string[] profileGuids;
            try
            {
                profileGuids = AssetDatabase.FindAssets("t:BuildProfile");
            }
            catch (Exception)
            {
                yield break; // BuildProfile type unavailable in this Unity version — Build Settings scan above still applies.
            }

            foreach (var guid in profileGuids)
            {
                BuildProfile profile;
                try
                {
                    profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(AssetDatabase.GUIDToAssetPath(guid));
                    if (profile == null || !profile.overrideGlobalScenes || profile.scenes == null)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var scene in profile.scenes)
                {
                    if (scene.enabled && seen.Add(scene.path))
                        yield return scene.path;
                }
            }
        }
    }
}
