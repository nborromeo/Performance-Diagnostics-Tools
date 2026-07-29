using System;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    [Serializable]
    public sealed class TerrainTileMergerOptimizerSettings
    {
        /// <summary>Tiles merged per block along the X axis (columns). Must equal BlockHeight — Unity terrain maps are always square. Minimum 1 (1x1 = no-op).</summary>
        public int BlockWidth = 2;

        /// <summary>Tiles merged per block along the Z axis (rows). Must equal BlockWidth — Unity terrain maps are always square. Minimum 1 (1x1 = no-op).</summary>
        public int BlockHeight = 2;
    }
}
