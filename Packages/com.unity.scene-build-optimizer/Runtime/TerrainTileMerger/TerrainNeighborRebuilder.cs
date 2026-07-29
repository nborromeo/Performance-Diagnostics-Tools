using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>
    /// Best-effort Terrain.SetNeighbors links across the coarser grid that results from merging some
    /// blocks while leaving others as leftovers.
    ///
    /// Terrain neighbor links are NOT persisted scene data — a Terrain component's serialized fields
    /// include allowAutoConnect and groupingID, but no left/right/top/bottomNeighbor fields at all.
    /// Unity's native auto-connect system re-derives them from scratch every time the scene loads,
    /// for any terrain with allowAutoConnect enabled, by matching groupingID and requiring an EXACT
    /// terrainData.size match between candidates. That's the only thing that survives a scene
    /// save/reload, so this optimizer leans on it rather than fighting it: every merged block from
    /// one generation pass ends up the same size as its siblings and keeps the template's
    /// groupingID/allowAutoConnect (see TerrainBlockMerger.CopyTerrainSettings), so Unity's own
    /// auto-connect correctly stitches merged-to-merged boundaries on every load — exactly the
    /// mechanism that already makes an untouched, uniform terrain grid seamless.
    ///
    /// The SetNeighbors calls here only help the CURRENT in-memory scene (e.g. inspecting it right
    /// after generation, before any save/reload) and are most useful for boundaries auto-connect can
    /// never establish on its own: a merged tile next to a leftover tile of a different size (a
    /// SizeMismatch, by auto-connect's own rule) has no path to a lasting fix — that boundary is a
    /// real Terrain engine limitation when the grid ends up with mixed tile sizes, not something a
    /// script can persist around.
    /// </summary>
    public static class TerrainNeighborRebuilder
    {
        public static void RebuildNeighbors(Scene duplicatedScene, IReadOnlyList<TerrainBlockMergeResult> mergedBlocks, IReadOnlyList<TerrainGridCell> leftovers)
        {
            var owner = new Dictionary<(int row, int col), Terrain>();
            var bounds = new Dictionary<Terrain, (int rowMin, int rowMax, int colMin, int colMax)>();

            foreach (var mergedBlock in mergedBlocks)
            {
                int rowMin = int.MaxValue, rowMax = int.MinValue, colMin = int.MaxValue, colMax = int.MinValue;
                foreach (var cell in mergedBlock.ConsumedCells)
                {
                    owner[(cell.Row, cell.Col)] = mergedBlock.MergedTerrain;
                    rowMin = Mathf.Min(rowMin, cell.Row);
                    rowMax = Mathf.Max(rowMax, cell.Row);
                    colMin = Mathf.Min(colMin, cell.Col);
                    colMax = Mathf.Max(colMax, cell.Col);
                }
                bounds[mergedBlock.MergedTerrain] = (rowMin, rowMax, colMin, colMax);
            }

            foreach (var cell in leftovers)
            {
                owner[(cell.Row, cell.Col)] = cell.Terrain;
                bounds[cell.Terrain] = (cell.Row, cell.Row, cell.Col, cell.Col);
            }

            foreach (var kv in bounds)
            {
                var terrain = kv.Key;
                var (rowMin, rowMax, colMin, colMax) = kv.Value;

                var left = LookupOwner(owner, rowMin, colMin - 1);
                var right = LookupOwner(owner, rowMin, colMax + 1);
                var top = LookupOwner(owner, rowMax + 1, colMin);
                var bottom = LookupOwner(owner, rowMin - 1, colMin);

                terrain.SetNeighbors(left, top, right, bottom);
                EditorUtility.SetDirty(terrain);
            }

            if (bounds.Count > 0)
                EditorSceneManager.MarkSceneDirty(duplicatedScene);
        }

        static Terrain LookupOwner(Dictionary<(int row, int col), Terrain> owner, int row, int col) =>
            owner.TryGetValue((row, col), out var terrain) ? terrain : null;
    }
}
