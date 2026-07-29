using System;
using UnityEngine;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>
    /// Merges a block's per-tile alphamaps (splat weights) into one alphamap against a shared
    /// <paramref name="mergedLayers"/> list (the union of every tile's own layers — see
    /// <see cref="TerrainBlockMerger"/>). Each tile's weights are remapped from its own layer
    /// indices onto the merged list's indices; any merged layer a given tile doesn't paint at all
    /// gets weight 0 in that tile's texels, which needs no renormalization since a tile's own
    /// weights already summed to ~1 across just its own layers.
    ///
    /// Unlike heightmaps, alphamap texels don't share an overlapping edge between neighbor tiles,
    /// so this is a plain tiling: merged resolution is tileResolution*N per axis, no edge
    /// de-duplication needed.
    /// </summary>
    public static class TerrainAlphamapMerger
    {
        public static float[,,] MergeAlphamaps(TerrainBlock block, TerrainLayer[] mergedLayers)
        {
            var templateData = block.Cells[0, 0].Terrain.terrainData;
            int canonicalWidth = templateData.alphamapWidth;
            int canonicalHeight = templateData.alphamapHeight;
            int mergedWidth = canonicalWidth * block.Width;
            int mergedHeight = canonicalHeight * block.Height;
            var merged = new float[mergedHeight, mergedWidth, mergedLayers.Length];

            for (int dr = 0; dr < block.Height; dr++)
            {
                for (int dc = 0; dc < block.Width; dc++)
                {
                    var terrainData = block.Cells[dr, dc].Terrain.terrainData;
                    var tileLayers = terrainData.terrainLayers;

                    // Read using THIS tile's own alphamapWidth/Height, not the template's — even
                    // when alphamapResolution matches across the block, the actual texture
                    // dimensions aren't guaranteed identical, and reading with a mismatched
                    // width/height argument silently misaligns that one tile's data.
                    int tileWidth = terrainData.alphamapWidth;
                    int tileHeight = terrainData.alphamapHeight;
                    var tileAlphamaps = terrainData.GetAlphamaps(0, 0, tileWidth, tileHeight);

                    var layerMap = new int[tileLayers.Length];
                    for (int i = 0; i < tileLayers.Length; i++)
                        layerMap[i] = Array.IndexOf(mergedLayers, tileLayers[i]);

                    int offsetY = dr * canonicalHeight;
                    int offsetX = dc * canonicalWidth;

                    for (int y = 0; y < tileHeight; y++)
                    {
                        for (int x = 0; x < tileWidth; x++)
                        {
                            for (int l = 0; l < tileLayers.Length; l++)
                                merged[offsetY + y, offsetX + x, layerMap[l]] = tileAlphamaps[y, x, l];
                        }
                    }
                }
            }

            return merged;
        }
    }
}
