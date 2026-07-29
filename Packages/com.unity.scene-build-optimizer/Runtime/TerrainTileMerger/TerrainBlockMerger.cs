using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>Result of successfully merging one TerrainBlock into a single Terrain.</summary>
    public sealed class TerrainBlockMergeResult
    {
        public readonly Terrain MergedTerrain;
        public readonly TerrainGridCell[] ConsumedCells;

        public TerrainBlockMergeResult(Terrain mergedTerrain, TerrainGridCell[] consumedCells)
        {
            MergedTerrain = mergedTerrain;
            ConsumedCells = consumedCells;
        }
    }

    /// <summary>
    /// Turns one validated TerrainBlock into a single merged Terrain/TerrainData asset: copies
    /// heights/alphamaps/trees/details from every tile into the merged asset, creates the merged
    /// GameObject, and destroys the original per-tile GameObjects from the duplicated scene.
    /// </summary>
    public static class TerrainBlockMerger
    {
        public static bool TryMerge(Scene duplicatedScene, TerrainBlock block, string sourceScenePath, string outputDir, SceneOptimizationReport report, out TerrainBlockMergeResult result)
        {
            result = null;
            var templateTerrain = block.Cells[0, 0].Terrain;
            var templateData = templateTerrain.terrainData;

            var sourcePaths = new List<string>(block.Height * block.Width);
            foreach (var cell in block.Cells)
            {
                var data = cell.Terrain.terrainData;
                string sourcePath = AssetDatabase.GetAssetPath(data);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    report.LogWarning("Terrain Tile Merger", $"Terrain '{cell.Terrain.name}' has no persisted TerrainData asset — block skipped.");
                    return false;
                }

                var referrers = AssetReferrerScanner.FindReferrersOutsideScene(sourcePath, sourceScenePath, duplicatedScene.path);
                if (referrers.Count > 0)
                {
                    Debug.Log($"Scene Build Optimizer: '{data.name}' has outside referrers — skipping block: {string.Join(", ", referrers)}");
                    report.LogWarning("Terrain Tile Merger",
                        $"TerrainData '{data.name}' (used by '{cell.Terrain.name}') is also referenced by: " +
                        $"{string.Join(", ", referrers)} — block skipped rather than duplicating a wider dependency chain.");
                    return false;
                }

                sourcePaths.Add(sourcePath);
            }

            AssetFolderUtility.EnsureFolderPath(outputDir);
            string mergedPath = AssetDatabase.GenerateUniqueAssetPath($"{outputDir}/{templateData.name}_Merged{block.Width}x{block.Height}.asset");

            var mergedData = new TerrainData { name = System.IO.Path.GetFileNameWithoutExtension(mergedPath) };
            AssetDatabase.CreateAsset(mergedData, mergedPath);
            AssetDatabase.ImportAsset(mergedPath);
            mergedData = AssetDatabase.LoadAssetAtPath<TerrainData>(mergedPath);

            PopulateMergedTerrainData(mergedData, templateData, block);

            var mergedGameObject = new GameObject($"{templateTerrain.name}_Merged{block.Width}x{block.Height}");
            mergedGameObject.transform.SetParent(templateTerrain.transform.parent, false);
            mergedGameObject.transform.position = templateTerrain.transform.position;
            mergedGameObject.layer = templateTerrain.gameObject.layer;
            mergedGameObject.tag = templateTerrain.gameObject.tag;
            mergedGameObject.isStatic = templateTerrain.gameObject.isStatic;

            var mergedTerrain = mergedGameObject.AddComponent<Terrain>();
            mergedTerrain.terrainData = mergedData;
            CopyTerrainSettings(templateTerrain, mergedTerrain);

            var mergedCollider = mergedGameObject.AddComponent<TerrainCollider>();
            mergedCollider.terrainData = mergedData;
            var templateCollider = templateTerrain.GetComponent<TerrainCollider>();
            if (templateCollider != null)
                mergedCollider.material = templateCollider.material;

            EditorUtility.SetDirty(mergedGameObject);

            var consumedCells = new TerrainGridCell[block.Height * block.Width];
            var consumedNames = new string[consumedCells.Length];
            int i = 0;
            foreach (var cell in block.Cells)
            {
                consumedCells[i] = cell;
                consumedNames[i] = cell.Terrain.name;
                i++;
                Object.DestroyImmediate(cell.Terrain.gameObject);
            }

            for (int p = 0; p < sourcePaths.Count; p++)
                report.LogCopiedAsset(sourcePaths[p], mergedPath);

            EditorSceneManager.MarkSceneDirty(duplicatedScene);

            report.LogChange("Terrain Tile Merger",
                $"Merged {block.Width}x{block.Height} block of tiles [{string.Join(", ", consumedNames)}] into '{mergedData.name}'.");

            result = new TerrainBlockMergeResult(mergedTerrain, consumedCells);
            return true;
        }

        static void PopulateMergedTerrainData(TerrainData mergedData, TerrainData templateData, TerrainBlock block)
        {
            int tileHeightmapResolution = templateData.heightmapResolution;
            mergedData.heightmapResolution = (tileHeightmapResolution - 1) * block.Width + 1;
            mergedData.size = new Vector3(templateData.size.x * block.Width, templateData.size.y, templateData.size.z * block.Height);
            mergedData.SetHeights(0, 0, TerrainHeightmapMerger.MergeHeights(block, tileHeightmapResolution));

            // Resolution set before layers: assigning terrainLayers first allocates default
            // alphamap textures at whatever resolution currently exists, and setting
            // alphamapResolution afterward would force Unity to resize/resample that default
            // content — even though SetAlphamaps immediately overwrites it, that resize-then-discard
            // churn is exactly the kind of place a stale texel-mapping artifact could come from.
            mergedData.alphamapResolution = templateData.alphamapResolution * block.Width;
            var mergedLayers = BuildUnionLayers(block);
            mergedData.terrainLayers = mergedLayers;
            var mergedAlphamaps = TerrainAlphamapMerger.MergeAlphamaps(block, mergedLayers);
            mergedData.SetAlphamaps(0, 0, mergedAlphamaps);

            var mergedTreePrototypes = TerrainTreeAndDetailMerger.BuildUnionTreePrototypes(block);
            mergedData.treePrototypes = mergedTreePrototypes;
            mergedData.treeInstances = TerrainTreeAndDetailMerger.MergeTreeInstances(block, mergedTreePrototypes);

            var mergedDetailPrototypes = TerrainTreeAndDetailMerger.BuildUnionDetailPrototypes(block);
            mergedData.detailPrototypes = mergedDetailPrototypes;
            mergedData.SetDetailResolution(templateData.detailResolution * block.Width, templateData.detailResolutionPerPatch);
            for (int layer = 0; layer < mergedDetailPrototypes.Length; layer++)
            {
                var mergedLayer = TerrainTreeAndDetailMerger.MergeDetailLayer(block, templateData.detailResolution, mergedDetailPrototypes, layer);
                mergedData.SetDetailLayer(0, 0, layer, mergedLayer);
            }
        }

        /// <summary>Union of every tile's terrainLayers in the block, first-seen order (row-major), deduplicated by reference.</summary>
        static TerrainLayer[] BuildUnionLayers(TerrainBlock block)
        {
            var union = new List<TerrainLayer>();
            foreach (var cell in block.Cells)
            {
                foreach (var layer in cell.Terrain.terrainData.terrainLayers)
                {
                    if (!union.Contains(layer))
                        union.Add(layer);
                }
            }
            return union.ToArray();
        }

        /// <summary>
        /// Offset applied to a merged terrain's groupingID, so Unity's native terrain auto-connect
        /// (which links same-groupingID, same-terrainData.size terrains on every scene load — see
        /// TerrainNeighborRebuilder) treats merged tiles as their own uniform group, separate from
        /// any still-original-size leftover tiles that share the source terrains' groupingID. Without
        /// this, a mismatched-size leftover sitting in the same group risks interfering with
        /// auto-connect for the whole group, including the merged tiles that should stitch cleanly.
        /// </summary>
        const int MergedGroupingIdOffset = 1000003;

        static void CopyTerrainSettings(Terrain source, Terrain destination)
        {
            destination.materialTemplate = source.materialTemplate;
            destination.drawInstanced = source.drawInstanced;
            destination.heightmapPixelError = source.heightmapPixelError;
            destination.basemapDistance = source.basemapDistance;
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.allowAutoConnect = source.allowAutoConnect;
            destination.groupingID = source.groupingID + MergedGroupingIdOffset;
            destination.drawHeightmap = source.drawHeightmap;
            destination.drawTreesAndFoliage = source.drawTreesAndFoliage;
            destination.treeDistance = source.treeDistance;
            destination.treeBillboardDistance = source.treeBillboardDistance;
            destination.treeCrossFadeLength = source.treeCrossFadeLength;
            destination.treeMaximumFullLODCount = source.treeMaximumFullLODCount;
            destination.detailObjectDistance = source.detailObjectDistance;
            destination.detailObjectDensity = source.detailObjectDensity;
            destination.preserveTreePrototypeLayers = source.preserveTreePrototypeLayers;
        }
    }
}
