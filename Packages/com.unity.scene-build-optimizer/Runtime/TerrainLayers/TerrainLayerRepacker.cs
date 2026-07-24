using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneBuildOptimizer.TerrainLayers
{
    public readonly struct RepackResult
    {
        public readonly TerrainLayer[] RemovedLayers;
        public readonly int RemainingLayerCount;

        public RepackResult(TerrainLayer[] removedLayers, int remainingLayerCount)
        {
            RemovedLayers = removedLayers;
            RemainingLayerCount = remainingLayerCount;
        }
    }

    /// <summary>
    /// Removes unused TerrainLayers from a (copied, safe-to-mutate) TerrainData and repacks its
    /// alphamaps to match. Only runs for terrains with something to strip, and only ever on the
    /// scene generator's copy — never the source asset.
    ///
    /// Uses TerrainData.GetAlphamaps/SetAlphamaps (the managed, always-correct path) rather than
    /// hand-writing raw bytes back out: repacking is one-time and only touches the already-reduced
    /// kept-layer set, so there's no perf case here worth the correctness risk of re-deriving the
    /// alphamap texture's byte layout on the write side.
    /// </summary>
    public static class TerrainLayerRepacker
    {
        public static RepackResult Repack(TerrainData terrainData, bool[] usedLayerMask)
        {
            var originalLayers = terrainData.terrainLayers;

            var keptIndices = new List<int>();
            var removedLayers = new List<TerrainLayer>();
            for (int i = 0; i < originalLayers.Length; i++)
            {
                if (usedLayerMask[i]) keptIndices.Add(i);
                else removedLayers.Add(originalLayers[i]);
            }

            if (removedLayers.Count == 0)
                return new RepackResult(Array.Empty<TerrainLayer>(), originalLayers.Length);

            if (keptIndices.Count == 0)
            {
                // Every layer scanned as unused - almost certainly a detection edge case (e.g. epsilon
                // misconfigured) rather than a genuinely blank terrain. Leave it untouched rather than
                // producing a terrain with zero layers.
                Debug.LogWarning(
                    $"Scene Build Optimizer: every layer on '{terrainData.name}' scanned as unused — leaving it untouched. " +
                    "Check the weight epsilon setting.");
                return new RepackResult(Array.Empty<TerrainLayer>(), originalLayers.Length);
            }

            int width = terrainData.alphamapWidth;
            int height = terrainData.alphamapHeight;
            var oldAlphamaps = terrainData.GetAlphamaps(0, 0, width, height);

            var keptLayers = new TerrainLayer[keptIndices.Count];
            var newAlphamaps = new float[height, width, keptIndices.Count];
            for (int k = 0; k < keptIndices.Count; k++)
            {
                keptLayers[k] = originalLayers[keptIndices[k]];
                int srcLayer = keptIndices[k];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    newAlphamaps[y, x, k] = oldAlphamaps[y, x, srcLayer];
            }

            terrainData.terrainLayers = keptLayers;
            terrainData.SetAlphamaps(0, 0, newAlphamaps);

            // Mutating terrainLayers/SetAlphamaps doesn't itself mark the asset dirty — without this,
            // AssetDatabase.SaveAssets() can skip writing these changes back to the .asset file.
            EditorUtility.SetDirty(terrainData);

            return new RepackResult(removedLayers.ToArray(), keptLayers.Length);
        }
    }
}
