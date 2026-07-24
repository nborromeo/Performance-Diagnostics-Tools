using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace SceneBuildOptimizer.TerrainLayers
{
    /// <summary>Determines, per TerrainLayer, whether it has any non-zero (above epsilon) weight anywhere on the terrain.</summary>
    public static class TerrainAlphamapUsageAnalyzer
    {
        const int k_PixelsPerBatch = 4096;
        const int k_BytesPerPixel = 4;

        /// <summary>
        /// Returns a mask the same length as <c>terrainData.terrainLayers</c>: true where the layer has
        /// alphamap weight above <paramref name="weightEpsilon"/> somewhere on the terrain.
        /// </summary>
        public static bool[] ComputeUsedLayerMask(TerrainData terrainData, float weightEpsilon)
        {
            int layerCount = terrainData.terrainLayers.Length;
            var used = new bool[layerCount];
            if (layerCount == 0) return used;

            byte byteThreshold = (byte)Mathf.Clamp(Mathf.RoundToInt(weightEpsilon * 255f), 0, 255);
            var textures = terrainData.alphamapTextures;

            for (int textureIndex = 0; textureIndex < textures.Length; textureIndex++)
            {
                int channelsInTexture = Mathf.Min(4, layerCount - textureIndex * 4);
                if (channelsInTexture <= 0) break;

                var maxPerChannel = ComputeMaxPerChannel(textures[textureIndex], channelsInTexture);
                for (int c = 0; c < channelsInTexture; c++)
                    used[textureIndex * 4 + c] = maxPerChannel[c] > byteThreshold;
            }

            return used;
        }

        static byte[] ComputeMaxPerChannel(Texture2D alphamapTexture, int channelCount)
        {
            if (!alphamapTexture.isReadable)
            {
                throw new System.InvalidOperationException(
                    $"Scene Build Optimizer: alphamap texture '{alphamapTexture.name}' is not CPU-readable; " +
                    "cannot scan it for terrain layer usage.");
            }

            var offsets = GraphicsFormatChannelLayout.GetByteOffsets(alphamapTexture.graphicsFormat);
            NativeArray<byte> pixels = alphamapTexture.GetRawTextureData<byte>();

            int pixelCount = pixels.Length / k_BytesPerPixel;
            int batchSize = Mathf.Max(1, k_PixelsPerBatch);
            int batchCount = (pixelCount + batchSize - 1) / batchSize;

            var maxR = new NativeArray<byte>(batchCount, Allocator.TempJob);
            var maxG = new NativeArray<byte>(batchCount, Allocator.TempJob);
            var maxB = new NativeArray<byte>(batchCount, Allocator.TempJob);
            var maxA = new NativeArray<byte>(batchCount, Allocator.TempJob);

            try
            {
                var job = new TerrainLayerUsageJob
                {
                    Pixels = pixels,
                    BytesPerPixel = k_BytesPerPixel,
                    RChannelOffset = offsets.R,
                    GChannelOffset = offsets.G,
                    BChannelOffset = offsets.B,
                    AChannelOffset = offsets.A,
                    ChannelCount = channelCount,
                    BatchSize = batchSize,
                    MaxPerBatchR = maxR,
                    MaxPerBatchG = maxG,
                    MaxPerBatchB = maxB,
                    MaxPerBatchA = maxA,
                };

                job.ScheduleBatch(pixelCount, batchSize).Complete();

                byte r = 0, g = 0, b = 0, a = 0;
                for (int i = 0; i < batchCount; i++)
                {
                    if (maxR[i] > r) r = maxR[i];
                    if (maxG[i] > g) g = maxG[i];
                    if (maxB[i] > b) b = maxB[i];
                    if (maxA[i] > a) a = maxA[i];
                }

                return new[] { r, g, b, a };
            }
            finally
            {
                maxR.Dispose();
                maxG.Dispose();
                maxB.Dispose();
                maxA.Dispose();
            }
        }
    }
}
