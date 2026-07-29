using System.Collections.Generic;
using UnityEngine;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>
    /// Merges tree/detail prototype lists into a union (tiles are allowed to use different sets —
    /// unlike splat layers, there's no per-pass draw-call budget for trees or detail meshes to
    /// worry about) and remaps each tile's instances/detail layers from its own local prototype
    /// indices and normalized/pixel space into the merged terrain's space, so nothing painted near a
    /// former tile boundary is lost, duplicated, or misattributed to the wrong prototype.
    /// </summary>
    public static class TerrainTreeAndDetailMerger
    {
        /// <summary>Union of every tile's treePrototypes in the block, first-seen order (row-major), deduplicated by prefab.</summary>
        public static TreePrototype[] BuildUnionTreePrototypes(TerrainBlock block)
        {
            var union = new List<TreePrototype>();
            foreach (var cell in block.Cells)
            {
                foreach (var prototype in cell.Terrain.terrainData.treePrototypes)
                {
                    if (!union.Exists(p => p.prefab == prototype.prefab))
                        union.Add(prototype);
                }
            }
            return union.ToArray();
        }

        /// <summary>Union of every tile's detailPrototypes in the block, first-seen order (row-major), deduplicated by prototype/prototypeTexture.</summary>
        public static DetailPrototype[] BuildUnionDetailPrototypes(TerrainBlock block)
        {
            var union = new List<DetailPrototype>();
            foreach (var cell in block.Cells)
            {
                foreach (var prototype in cell.Terrain.terrainData.detailPrototypes)
                {
                    if (!union.Exists(p => p.prototype == prototype.prototype && p.prototypeTexture == prototype.prototypeTexture))
                        union.Add(prototype);
                }
            }
            return union.ToArray();
        }

        public static TreeInstance[] MergeTreeInstances(TerrainBlock block, TreePrototype[] mergedPrototypes)
        {
            var merged = new List<TreeInstance>();

            for (int dr = 0; dr < block.Height; dr++)
            {
                for (int dc = 0; dc < block.Width; dc++)
                {
                    var terrainData = block.Cells[dr, dc].Terrain.terrainData;
                    var tilePrototypes = terrainData.treePrototypes;
                    var indexMap = new int[tilePrototypes.Length];
                    for (int i = 0; i < tilePrototypes.Length; i++)
                        indexMap[i] = System.Array.FindIndex(mergedPrototypes, p => p.prefab == tilePrototypes[i].prefab);

                    foreach (var instance in terrainData.treeInstances)
                    {
                        var remapped = instance;
                        remapped.prototypeIndex = indexMap[instance.prototypeIndex];
                        remapped.position = new Vector3(
                            (dc + instance.position.x) / block.Width,
                            instance.position.y,
                            (dr + instance.position.z) / block.Height);
                        merged.Add(remapped);
                    }
                }
            }

            return merged.ToArray();
        }

        /// <summary>
        /// Detail texels don't share an edge between neighbor tiles (same as alphamaps) — plain
        /// tiling. Tiles that don't have <paramref name="mergedPrototypes"/>[mergedLayerIndex] at
        /// all contribute zero (nothing painted) for their region rather than being skipped.
        /// </summary>
        public static int[,] MergeDetailLayer(TerrainBlock block, int tileDetailResolution, DetailPrototype[] mergedPrototypes, int mergedLayerIndex)
        {
            int mergedWidth = tileDetailResolution * block.Width;
            int mergedHeight = tileDetailResolution * block.Height;
            var merged = new int[mergedHeight, mergedWidth];
            var targetPrototype = mergedPrototypes[mergedLayerIndex];

            for (int dr = 0; dr < block.Height; dr++)
            {
                for (int dc = 0; dc < block.Width; dc++)
                {
                    var terrainData = block.Cells[dr, dc].Terrain.terrainData;
                    var tilePrototypes = terrainData.detailPrototypes;
                    int localIndex = System.Array.FindIndex(tilePrototypes, p => p.prototype == targetPrototype.prototype && p.prototypeTexture == targetPrototype.prototypeTexture);
                    if (localIndex < 0)
                        continue; // this tile doesn't use this prototype — leave its region at 0 (nothing painted)

                    var tileLayer = terrainData.GetDetailLayer(0, 0, tileDetailResolution, tileDetailResolution, localIndex);

                    int offsetY = dr * tileDetailResolution;
                    int offsetX = dc * tileDetailResolution;

                    for (int y = 0; y < tileDetailResolution; y++)
                    {
                        for (int x = 0; x < tileDetailResolution; x++)
                            merged[offsetY + y, offsetX + x] = tileLayer[y, x];
                    }
                }
            }

            return merged;
        }
    }
}
