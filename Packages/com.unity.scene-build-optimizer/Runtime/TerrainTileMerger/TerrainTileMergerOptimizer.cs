using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>
    /// Merges NxN grids of adjacent Terrain tiles (discovered from each tile's world-space
    /// footprint — see <see cref="TerrainGridDiscovery"/>) into fewer, larger Terrain/TerrainData
    /// assets, with no seam in the merged heightmap or splatmap. Must run after
    /// <see cref="TerrainLayers.TerrainLayerOptimizer"/> — see <see cref="Order"/> — since stripping
    /// each tile's unused layers first can let more blocks satisfy the merge's draw-call-pass budget
    /// (see <see cref="TerrainBlockValidator"/>).
    /// </summary>
    [InitializeOnLoad]
    public sealed class TerrainTileMergerOptimizer : ISceneOptimizer
    {
        public const string OptimizerId = "SceneBuildOptimizer.TerrainTileMergerOptimizer";

        static readonly TerrainTileMergerOptimizer s_Instance = new TerrainTileMergerOptimizer();

        static TerrainTileMergerOptimizer() => SceneOptimizerRegistry.Register(s_Instance);

        public string Id => OptimizerId;
        public string Name => "Terrain Tile Merger";
        public int Order => 200; // must run after TerrainLayerOptimizer (100) — stripping unused layers first can let more blocks merge

        public bool HasSettings => true;

        public object CreateDefaultSettings() => new TerrainTileMergerOptimizerSettings();

        public void DrawSettingsGUI(object settingsObj)
        {
            var settings = (TerrainTileMergerOptimizerSettings)settingsObj;
            EditorGUILayout.LabelField(
                "Merges NxN grids of adjacent Terrain tiles (adjacency detected from tile position/size) into one Terrain/TerrainData each. Block width and height must match — Unity terrain maps are always square. Tiles with different splat layers can still merge as long as the combined layer count doesn't need more draw call passes than the block's costliest tile already needed alone (4 layers per pass).",
                EditorStyles.wordWrappedMiniLabel);
            settings.BlockWidth = Mathf.Max(1, EditorGUILayout.IntField("Block width (cols)", settings.BlockWidth));
            settings.BlockHeight = Mathf.Max(1, EditorGUILayout.IntField("Block height (rows)", settings.BlockHeight));
        }

        public void Execute(Scene duplicatedScene, string sourceScenePath, string sceneAssetDir, object settingsObj, SceneOptimizationReport report)
        {
            var settings = (TerrainTileMergerOptimizerSettings)settingsObj;
            if (settings.BlockWidth <= 1 && settings.BlockHeight <= 1)
            {
                Debug.Log("Scene Build Optimizer: Terrain Tile Merger block size is 1x1 — nothing to do.");
                return;
            }

            EditorUtility.DisplayProgressBar("Terrain Tile Merger", "Finding Terrain components in the duplicated scene...", 0f);
            var allTerrains = new List<Terrain>();
            foreach (var root in duplicatedScene.GetRootGameObjects())
                allTerrains.AddRange(root.GetComponentsInChildren<Terrain>(true));

            Debug.Log($"Scene Build Optimizer: Terrain Tile Merger found {allTerrains.Count} Terrain component(s) in the duplicated scene.");

            string outputDir = $"{sceneAssetDir}/MergedTerrainData";

            EditorUtility.DisplayProgressBar("Terrain Tile Merger", $"Discovering terrain grids from {allTerrains.Count} tile(s)...", 0f);
            var grids = TerrainGridDiscovery.DiscoverGrids(allTerrains);

            // Chunk every grid up front so the progress bar below can report an accurate "block X of
            // Y" — otherwise there'd be no way to know the total ahead of time.
            var pendingGrids = new List<(TerrainGrid grid, List<TerrainBlock> blocks, List<TerrainGridCell> leftovers)>();
            int totalBlocks = 0;
            foreach (var grid in grids)
            {
                if (grid.Cells.Count < 2)
                    continue; // single, unlinked terrain — nothing to merge

                var blocks = TerrainBlockChunker.ChunkGrid(grid, settings.BlockWidth, settings.BlockHeight, out var leftovers);
                pendingGrids.Add((grid, blocks, leftovers));
                totalBlocks += blocks.Count;
            }

            int mergedBlockCount = 0;
            int blocksProcessed = 0;
            foreach (var (_, blocks, leftovers) in pendingGrids)
            {
                var mergeResults = new List<TerrainBlockMergeResult>();
                var unmergedCells = new List<TerrainGridCell>(leftovers);

                foreach (var block in blocks)
                {
                    string blockLabel = block.Cells[0, 0].Terrain.name;
                    EditorUtility.DisplayProgressBar("Terrain Tile Merger",
                        $"Merging block {blocksProcessed + 1} of {totalBlocks} (starting at '{blockLabel}')...",
                        totalBlocks == 0 ? 0f : (float)blocksProcessed / totalBlocks);
                    blocksProcessed++;

                    if (!TerrainBlockValidator.Validate(block, out string reason))
                    {
                        Debug.Log($"Scene Build Optimizer: Terrain Tile Merger skipping block starting at '{blockLabel}' — {reason}");
                        report.LogWarning("Terrain Tile Merger", $"Block starting at '{blockLabel}' skipped: {reason}");
                        unmergedCells.AddRange(block.Cells.Cast<TerrainGridCell>());
                        continue;
                    }

                    if (!TerrainBlockMerger.TryMerge(duplicatedScene, block, sourceScenePath, outputDir, report, out var mergeResult))
                    {
                        unmergedCells.AddRange(block.Cells.Cast<TerrainGridCell>());
                        continue;
                    }

                    mergeResults.Add(mergeResult);
                    mergedBlockCount++;
                }

                if (mergeResults.Count > 0)
                {
                    EditorUtility.DisplayProgressBar("Terrain Tile Merger", "Re-linking terrain neighbors...", 1f);
                    TerrainNeighborRebuilder.RebuildNeighbors(duplicatedScene, mergeResults, unmergedCells);
                }
            }

            Debug.Log($"Scene Build Optimizer: Terrain Tile Merger merged {mergedBlockCount} block(s) across {grids.Count} discovered grid(s).");
        }
    }
}
