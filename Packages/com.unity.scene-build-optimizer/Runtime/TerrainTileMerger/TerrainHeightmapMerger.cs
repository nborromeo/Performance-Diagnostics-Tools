namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>
    /// Merges a block's per-tile heightmaps into one seamless heightmap, following Unity's standard
    /// multi-tile convention: adjacent tiles share one overlapping edge row/column of height values,
    /// so a merged NxM block's resolution is (tileRes-1)*N + 1 rather than a plain tileRes*N.
    /// </summary>
    public static class TerrainHeightmapMerger
    {
        public static float[,] MergeHeights(TerrainBlock block, int tileHeightmapResolution)
        {
            int mergedResX = (tileHeightmapResolution - 1) * block.Width + 1;
            int mergedResY = (tileHeightmapResolution - 1) * block.Height + 1;
            var merged = new float[mergedResY, mergedResX];

            for (int dr = 0; dr < block.Height; dr++)
            {
                for (int dc = 0; dc < block.Width; dc++)
                {
                    var terrainData = block.Cells[dr, dc].Terrain.terrainData;
                    var tileHeights = terrainData.GetHeights(0, 0, tileHeightmapResolution, tileHeightmapResolution);

                    int offsetY = dr * (tileHeightmapResolution - 1);
                    int offsetX = dc * (tileHeightmapResolution - 1);

                    // Paste the full tile, including its shared edge row/column — the adjacent
                    // tile's paste overwrites the same cells with the same values (Unity keeps
                    // neighbor edges in sync), so no special-casing of the last row/column is needed.
                    for (int y = 0; y < tileHeightmapResolution; y++)
                    {
                        for (int x = 0; x < tileHeightmapResolution; x++)
                            merged[offsetY + y, offsetX + x] = tileHeights[y, x];
                    }
                }
            }

            return merged;
        }
    }
}
