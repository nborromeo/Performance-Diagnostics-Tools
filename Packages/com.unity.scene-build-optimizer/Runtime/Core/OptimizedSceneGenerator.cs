using System.IO;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Orchestrates turning an authoring scene into a persistent, optimized scene asset: duplicate
    /// the scene file, run every enabled <see cref="ISceneOptimizer"/> against the duplicate, save it,
    /// and record an <see cref="OptimizedSceneManifest"/> sidecar for cheap staleness checks later.
    ///
    /// The source scene and its assets are never opened for writing by this path.
    /// </summary>
    public static class OptimizedSceneGenerator
    {
        /// <summary>Default optimized-scene location for a source scene at "Assets/Scenes/Level01.unity": "Assets/Scenes/Optimized/Level01/Level01.unity".</summary>
        public static string GetDefaultOptimizedScenePath(string sourceScenePath)
        {
            string sceneName = Path.GetFileNameWithoutExtension(sourceScenePath);
            string sourceDir = Path.GetDirectoryName(sourceScenePath)?.Replace('\\', '/');
            return $"{sourceDir}/Optimized/{sceneName}/{sceneName}.unity";
        }

        /// <summary>
        /// Generates (or regenerates) the optimized scene for <paramref name="sourceScenePath"/>.
        /// Prompts to save the currently open scene(s) first if they have unsaved changes, since this
        /// closes whatever's open to load the duplicate; restores the previously active scene afterward.
        /// </summary>
        /// <param name="targetProfile">
        /// Which BuildProfile's optimizer overrides to resolve against (null = project-wide defaults
        /// only). Deliberately explicit rather than defaulting to whatever Unity's Build Profiles window
        /// currently has selected as "active" — that's easy-to-miss ambient state, unrelated to what a
        /// caller here actually intends. Pass <see cref="BuildProfile.GetActiveBuildProfile"/> yourself
        /// if that's genuinely what you want (e.g. the pre-build check does, to match the real build).
        /// </param>
        public static SceneOptimizationReport Generate(string sourceScenePath, string optimizedScenePath = null, BuildProfile targetProfile = null)
        {
            var report = new SceneOptimizationReport();

            if (!File.Exists(sourceScenePath))
            {
                report.LogWarning("Scene Build Optimizer", $"Source scene not found: {sourceScenePath}");
                return report;
            }

            optimizedScenePath ??= GetDefaultOptimizedScenePath(sourceScenePath);
            string sceneAssetDir = Path.GetDirectoryName(optimizedScenePath)?.Replace('\\', '/');

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                report.LogWarning("Scene Build Optimizer", "Cancelled — the currently open scene has unsaved changes.");
                return report;
            }

            string previouslyActiveScenePath = EditorSceneManager.GetActiveScene().path;

            AssetFolderUtility.EnsureFolderPath(sceneAssetDir);

            // Deterministic path, overwritten in place (not delete+recreate) so re-running preserves
            // the optimized scene's GUID — otherwise any existing reference to it (e.g. in a
            // BuildProfile's scene override list, which is GUID-based) would silently break.
            if (!AssetCopyUtility.CopyOrOverwrite(sourceScenePath, optimizedScenePath))
            {
                report.LogWarning("Scene Build Optimizer", $"Failed to copy scene to '{optimizedScenePath}'.");
                return report;
            }

            var duplicatedScene = EditorSceneManager.OpenScene(optimizedScenePath, OpenSceneMode.Single);

            var manifest = new OptimizedSceneManifest
            {
                SourceScenePath = sourceScenePath,
                SourceSceneHash = OptimizedSceneManifest.ComputeFileHash(sourceScenePath),
            };

            var settingsContainer = SceneOptimizerSettingsProvider.GetOrCreateSettings();
            Debug.Log(targetProfile != null
                ? $"Scene Build Optimizer: generating '{optimizedScenePath}' against profile '{targetProfile.name}'."
                : $"Scene Build Optimizer: generating '{optimizedScenePath}' against project-wide defaults (no profile).");

            foreach (var optimizer in SceneOptimizerRegistry.Optimizers)
            {
                // A profile override (if targetProfile has one for this optimizer) decides both
                // whether it runs and its settings; otherwise the project-wide default applies.
                var entry = settingsContainer.ResolveEntry(optimizer.Id, optimizer.CreateDefaultSettings, targetProfile);
                Debug.Log($"Scene Build Optimizer: optimizer '{optimizer.Name}' enabled={entry.Enabled} for this generation.");
                if (!entry.Enabled) continue;
                optimizer.Execute(duplicatedScene, sourceScenePath, sceneAssetDir, entry.Settings, report);
            }

            EditorSceneManager.SaveScene(duplicatedScene);

            foreach (var copied in report.CopiedAssets)
            {
                manifest.CopiedAssets.Add(new OptimizedSceneManifest.CopiedAsset
                {
                    SourcePath = copied.SourcePath,
                    OptimizedPath = copied.OptimizedPath,
                    SourceHash = OptimizedSceneManifest.ComputeFileHash(copied.SourcePath),
                });
            }
            manifest.Save(optimizedScenePath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!string.IsNullOrEmpty(previouslyActiveScenePath) && File.Exists(previouslyActiveScenePath))
                EditorSceneManager.OpenScene(previouslyActiveScenePath, OpenSceneMode.Single);

            return report;
        }

        /// <summary>True if no optimized scene/manifest exists yet, or the source scene/copied assets changed since the last generation.</summary>
        public static bool IsStale(string sourceScenePath, string optimizedScenePath = null)
        {
            optimizedScenePath ??= GetDefaultOptimizedScenePath(sourceScenePath);
            var manifest = OptimizedSceneManifest.Load(optimizedScenePath);
            return manifest == null || manifest.IsStale();
        }
    }
}
