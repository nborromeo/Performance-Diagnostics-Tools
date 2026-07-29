using System.Collections.Generic;
using UnityEngine;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>
    /// Checks that every tile in a candidate TerrainBlock is compatible enough to merge without
    /// resampling: identical heightmap/alphamap/detail resolutions and size, and a splat-layer
    /// budget that doesn't get worse (see <see cref="LayersCompatible"/>). Tree and detail
    /// prototypes are allowed to differ freely between tiles — see
    /// <see cref="TerrainTreeAndDetailMerger"/>, which merges them into a union — since unlike
    /// splat layers there's no per-pass draw-call budget to protect there. Any remaining mismatch
    /// fails the whole block — no reconciliation is attempted for resolution/size, per design
    /// (mismatched blocks are left unmerged and reported as a warning).
    /// </summary>
    public static class TerrainBlockValidator
    {
        /// <summary>Terrain packs up to 4 splat layers per alphamap texture/draw call pass.</summary>
        const int LayersPerPass = 4;

        /// <summary>
        /// Unity clamps TerrainData.heightmapResolution to exactly one of these values — a merge
        /// that lands on anything else would silently get resampled/clamped by Unity, reintroducing
        /// the exact seam risk this optimizer exists to avoid, so it must be rejected up front
        /// instead.
        /// </summary>
        static readonly int[] s_AllowedHeightmapResolutions = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };

        public static bool Validate(TerrainBlock block, out string reason)
        {
            // Heightmap, alphamap and detail maps are always square textures in Unity (a single
            // resolution value covers both axes) — a non-square block (BlockWidth != BlockHeight)
            // would need a non-square merged map, which TerrainData cannot represent, regardless of
            // how the individual tiles' world-space size or resolution otherwise line up.
            if (block.Width != block.Height)
            {
                reason = $"Block is {block.Width}x{block.Height} (non-square) — Unity terrain heightmaps/alphamaps are always square, so only NxN blocks (e.g. 2x2, 3x3) can be merged into a single Terrain.";
                return false;
            }

            var first = block.Cells[0, 0].Terrain.terrainData;

            int mergedHeightmapResolution = (first.heightmapResolution - 1) * block.Width + 1;
            if (System.Array.IndexOf(s_AllowedHeightmapResolutions, mergedHeightmapResolution) < 0)
            {
                reason = $"Merging a {block.Width}x{block.Height} block of {first.heightmapResolution}-resolution tiles would need a {mergedHeightmapResolution} heightmap, which isn't one of Unity's allowed resolutions ({string.Join(", ", s_AllowedHeightmapResolutions)}) — skipped rather than let Unity resample/clamp it.";
                return false;
            }

            if (!LayersCompatible(block, out reason))
                return false;

            foreach (var cell in block.Cells)
            {
                var terrain = cell.Terrain;
                var data = terrain.terrainData;

                if (data == null)
                {
                    reason = $"Terrain '{terrain.name}' has no TerrainData assigned.";
                    return false;
                }
                if (data.heightmapResolution != first.heightmapResolution)
                {
                    reason = $"heightmapResolution mismatch ('{terrain.name}': {data.heightmapResolution} vs '{block.Cells[0, 0].Terrain.name}': {first.heightmapResolution}).";
                    return false;
                }
                if (data.size != first.size)
                {
                    reason = $"size mismatch ('{terrain.name}': {data.size} vs '{block.Cells[0, 0].Terrain.name}': {first.size}).";
                    return false;
                }
                if (data.alphamapResolution != first.alphamapResolution)
                {
                    reason = $"alphamapResolution mismatch ('{terrain.name}': {data.alphamapResolution} vs '{block.Cells[0, 0].Terrain.name}': {first.alphamapResolution}).";
                    return false;
                }
                // alphamapResolution matching doesn't strictly guarantee the actual texture
                // dimensions match too — check those directly, since TerrainAlphamapMerger reads
                // each tile with its own alphamapWidth/Height and a mismatch here would misalign
                // that tile's data in the merged output.
                if (data.alphamapWidth != first.alphamapWidth || data.alphamapHeight != first.alphamapHeight)
                {
                    reason = $"alphamapWidth/Height mismatch ('{terrain.name}': {data.alphamapWidth}x{data.alphamapHeight} vs '{block.Cells[0, 0].Terrain.name}': {first.alphamapWidth}x{first.alphamapHeight}).";
                    return false;
                }
                if (data.detailResolution != first.detailResolution)
                {
                    reason = $"detailResolution mismatch ('{terrain.name}': {data.detailResolution} vs '{block.Cells[0, 0].Terrain.name}': {first.detailResolution}).";
                    return false;
                }
                if (data.detailResolutionPerPatch != first.detailResolutionPerPatch)
                {
                    reason = $"detailResolutionPerPatch mismatch ('{terrain.name}': {data.detailResolutionPerPatch} vs '{block.Cells[0, 0].Terrain.name}': {first.detailResolutionPerPatch}).";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// A block merges its tiles' splat layers into one union list (see
        /// <see cref="TerrainBlockMerger"/>) rather than requiring identical layer lists — but only
        /// when doing so doesn't need MORE alphamap draw call passes than the worst tile in the
        /// block already needed on its own. E.g. two 2-layer tiles merge fine (1 pass either way); a
        /// 5-layer tile absorbing a 2-layer tile is fine too (already 2 passes, still 2 passes after);
        /// two 3-layer tiles do not merge (1 pass each alone, 2 passes once combined).
        /// </summary>
        static bool LayersCompatible(TerrainBlock block, out string reason)
        {
            var unionLayers = new HashSet<TerrainLayer>();
            int maxIndividualPasses = 0;

            foreach (var cell in block.Cells)
            {
                var layers = cell.Terrain.terrainData.terrainLayers;
                foreach (var layer in layers)
                    unionLayers.Add(layer);
                maxIndividualPasses = Mathf.Max(maxIndividualPasses, PassesFor(layers.Length));
            }

            int mergedPasses = PassesFor(unionLayers.Count);
            if (mergedPasses > maxIndividualPasses)
            {
                reason = $"Merging would need {mergedPasses} splat draw call pass(es) ({unionLayers.Count} combined layer(s)), more than the {maxIndividualPasses} pass(es) any single tile in the block already needed — merging would make rendering worse, not better.";
                return false;
            }

            reason = null;
            return true;
        }

        static int PassesFor(int layerCount) => (layerCount + LayersPerPass - 1) / LayersPerPass;
    }
}
