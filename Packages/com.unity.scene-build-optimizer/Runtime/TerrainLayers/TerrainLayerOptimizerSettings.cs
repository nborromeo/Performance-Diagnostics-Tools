using System;

namespace SceneBuildOptimizer.TerrainLayers
{
    [Serializable]
    public sealed class TerrainLayerOptimizerSettings
    {
        /// <summary>A layer with max alphamap weight at or below this (0-1) is considered unused. Default 0 = exactly zero everywhere.</summary>
        public float WeightEpsilon = 0f;
    }
}
