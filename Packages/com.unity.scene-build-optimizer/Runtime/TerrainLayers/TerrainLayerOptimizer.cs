using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuildOptimizer.TerrainLayers
{
    /// <summary>
    /// Strips TerrainLayers whose alphamap weight is (near-)zero everywhere on the terrain, then
    /// repacks the remaining layers — e.g. a terrain authored with 8 layers where only 2 are ever
    /// painted is rendered with 2 alphamap-blend passes instead of 8 - once optimized.
    /// </summary>
    [InitializeOnLoad]
    public sealed class TerrainLayerOptimizer : ISceneOptimizer
    {
        public const string OptimizerId = "SceneBuildOptimizer.TerrainLayerOptimizer";

        static readonly TerrainLayerOptimizer s_Instance = new TerrainLayerOptimizer();

        static TerrainLayerOptimizer() => SceneOptimizerRegistry.Register(s_Instance);

        public string Id => OptimizerId;
        public string Name => "Terrain Layer Optimizer";
        public int Order => 100; // runs before TerrainTileMergerOptimizer: stripping unused layers first shrinks each tile's own layer count, which can let more blocks satisfy the merger's draw-call-pass budget

        public bool HasSettings => true;

        public object CreateDefaultSettings() => new TerrainLayerOptimizerSettings();

        public void DrawSettingsGUI(object settingsObj)
        {
            var settings = (TerrainLayerOptimizerSettings)settingsObj;
            EditorGUILayout.LabelField("A layer is considered unused when its alphamap weight never exceeds this value.", EditorStyles.wordWrappedMiniLabel);
            settings.WeightEpsilon = EditorGUILayout.Slider("Weight epsilon", settings.WeightEpsilon, 0f, 0.1f);
        }

        public void Execute(Scene duplicatedScene, string sourceScenePath, string sceneAssetDir, object settingsObj, SceneOptimizationReport report)
        {
            var settings = (TerrainLayerOptimizerSettings)settingsObj;
            var processed = new HashSet<TerrainData>();
            string outputDir = $"{sceneAssetDir}/OptimizedTerrainData";

            int terrainCount = 0;
            foreach (var root in duplicatedScene.GetRootGameObjects())
            {
                foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                {
                    terrainCount++;
                    var terrainData = terrain.terrainData;
                    if (terrainData == null)
                    {
                        Debug.Log($"Scene Build Optimizer: Terrain '{terrain.name}' has no TerrainData assigned — skipping.");
                        continue;
                    }
                    if (!processed.Add(terrainData))
                    {
                        Debug.Log($"Scene Build Optimizer: Terrain '{terrain.name}' shares TerrainData '{terrainData.name}' already handled by another Terrain in this scene — skipping duplicate work.");
                        continue;
                    }

                    Debug.Log($"Scene Build Optimizer: processing Terrain '{terrain.name}' with TerrainData '{terrainData.name}' ({terrainData.terrainLayers.Length} layers), epsilon={settings.WeightEpsilon}.");
                    ProcessTerrainData(duplicatedScene, terrain, terrainData, sourceScenePath, outputDir, settings, report);
                }
            }

            Debug.Log($"Scene Build Optimizer: Terrain Layer Optimizer found {terrainCount} Terrain component(s) in the duplicated scene.");
        }

        static void ProcessTerrainData(Scene duplicatedScene, Terrain firstTerrain, TerrainData terrainData,
            string sourceScenePath, string outputDir, TerrainLayerOptimizerSettings settings, SceneOptimizationReport report)
        {
            string sourcePath = AssetDatabase.GetAssetPath(terrainData);
            if (string.IsNullOrEmpty(sourcePath))
            {
                report.LogWarning("Terrain Layer Optimizer", $"Terrain '{firstTerrain.name}' has no persisted TerrainData asset — skipped.");
                return;
            }

            // Exclude both the authoring scene AND the optimized scene currently being (re)generated —
            // duplicatedScene.path is the latter, and at this point it's already been freshly
            // overwritten from the source (pre-optimization), so it would otherwise always look like a
            // false-positive external referrer of the very TerrainData it's about to stop using.
            var referrers = AssetReferrerScanner.FindReferrersOutsideScene(sourcePath, sourceScenePath, duplicatedScene.path);
            if (referrers.Count > 0)
            {
                Debug.Log($"Scene Build Optimizer: '{terrainData.name}' has outside referrers — skipping: {string.Join(", ", referrers)}");
                report.LogWarning("Terrain Layer Optimizer",
                    $"TerrainData '{terrainData.name}' (used by '{firstTerrain.name}') is also referenced by: " +
                    $"{string.Join(", ", referrers)} — skipped rather than duplicating a wider dependency chain.");
                return;
            }

            var usedMask = TerrainAlphamapUsageAnalyzer.ComputeUsedLayerMask(terrainData, settings.WeightEpsilon);
            int usedCount = System.Array.FindAll(usedMask, u => u).Length;
            Debug.Log($"Scene Build Optimizer: '{terrainData.name}' usage scan: {usedCount}/{usedMask.Length} layers used " +
                $"[{string.Join(",", System.Array.ConvertAll(usedMask, u => u ? "1" : "0"))}].");

            bool anyUnused = System.Array.Exists(usedMask, used => !used);
            if (!anyUnused)
            {
                Debug.Log($"Scene Build Optimizer: '{terrainData.name}' — every layer scanned as used, nothing to strip.");
                return; // nothing to strip, don't even copy the asset
            }

            AssetFolderUtility.EnsureFolderPath(outputDir);

            // Deterministic path. Always a fresh delete+recreate (new GUID each time, see
            // AssetCopyUtility.ForceFreshCopy) rather than an in-place overwrite: nothing outside this
            // same regeneration holds a persistent reference to this copy's GUID (we repoint every
            // Terrain/TerrainCollider below, every run), and TerrainData's embedded alphamap textures
            // aren't reliably refreshed by a raw file overwrite + forced reimport the way a plain scene
            // file is.
            string copyPath = $"{outputDir}/{terrainData.name}.asset";
            if (!AssetCopyUtility.ForceFreshCopy(sourcePath, copyPath))
            {
                report.LogWarning("Terrain Layer Optimizer", $"Failed to copy TerrainData '{terrainData.name}' to '{copyPath}' — skipped.");
                return;
            }

            AssetDatabase.ImportAsset(copyPath);
            var terrainDataCopy = AssetDatabase.LoadAssetAtPath<TerrainData>(copyPath);
            Debug.Log($"Scene Build Optimizer: copied '{sourcePath}' -> '{copyPath}', loaded copy = {(terrainDataCopy != null ? terrainDataCopy.name : "NULL")}.");
            report.LogCopiedAsset(sourcePath, copyPath);

            var result = TerrainLayerRepacker.Repack(terrainDataCopy, usedMask);
            Debug.Log($"Scene Build Optimizer: repack result — removed {result.RemovedLayers.Length} layer(s), {result.RemainingLayerCount} remaining.");
            if (result.RemovedLayers.Length == 0)
                return; // repacker declined (e.g. every layer scanned unused) — already warned there

            // Repoint every Terrain/TerrainCollider in the duplicated scene that used the source TerrainData.
            int repointedTerrains = 0, repointedColliders = 0;
            foreach (var root in duplicatedScene.GetRootGameObjects())
            {
                foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                {
                    if (terrain.terrainData == terrainData)
                    {
                        terrain.terrainData = terrainDataCopy;
                        EditorUtility.SetDirty(terrain);
                        repointedTerrains++;
                    }
                }
                foreach (var collider in root.GetComponentsInChildren<TerrainCollider>(true))
                {
                    if (collider.terrainData == terrainData)
                    {
                        collider.terrainData = terrainDataCopy;
                        EditorUtility.SetDirty(collider);
                        repointedColliders++;
                    }
                }
            }

            // Reassigning a component field doesn't itself mark the scene dirty — without this,
            // EditorSceneManager.SaveScene can treat the scene as unchanged and skip writing these
            // reassignments back to the .unity file.
            if (repointedTerrains > 0 || repointedColliders > 0)
                EditorSceneManager.MarkSceneDirty(duplicatedScene);

            Debug.Log($"Scene Build Optimizer: repointed {repointedTerrains} Terrain(s) and {repointedColliders} TerrainCollider(s) from '{terrainData.name}' to '{terrainDataCopy.name}'.");

            var removedNames = System.Array.ConvertAll(result.RemovedLayers, l => l != null ? l.name : "<missing>");
            report.LogChange("Terrain Layer Optimizer",
                $"'{terrainData.name}': removed {result.RemovedLayers.Length} unused layer(s) " +
                $"[{string.Join(", ", removedNames)}], {result.RemainingLayerCount} remaining.");
        }
    }
}
