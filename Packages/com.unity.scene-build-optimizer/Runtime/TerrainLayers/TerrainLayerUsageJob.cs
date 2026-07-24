using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace SceneBuildOptimizer.TerrainLayers
{
    /// <summary>
    /// Scans raw alphamap pixel bytes and computes, per batch, the max value seen in each of up to
    /// four channels — used to detect whether a TerrainLayer's weight is zero (unused) everywhere.
    ///
    /// Operates directly on the NativeArray view backing the texture's native memory (from
    /// <c>Texture2D.GetRawTextureData&lt;byte&gt;()</c>) so scanning a terrain's alphamaps never pays
    /// for a managed copy — a 2049² 8-layer terrain would otherwise cost ~1.3GB via TerrainData.GetAlphamaps.
    ///
    /// Writes one max-per-channel result per batch (not per-pixel) to avoid any cross-thread races;
    /// the caller reduces the small per-batch array on the main thread.
    /// </summary>
    [BurstCompile]
    public unsafe struct TerrainLayerUsageJob : IJobParallelForBatch
    {
        [ReadOnly] public NativeArray<byte> Pixels;
        public int BytesPerPixel;
        public int RChannelOffset;
        public int GChannelOffset;
        public int BChannelOffset;
        public int AChannelOffset;

        /// <summary>How many of the 4 channels in this texture map to a real TerrainLayer (1-4; the last alphamap texture may have fewer than 4 if layerCount isn't a multiple of 4).</summary>
        public int ChannelCount;

        public int BatchSize;

        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> MaxPerBatchR;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> MaxPerBatchG;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> MaxPerBatchB;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> MaxPerBatchA;

        public void Execute(int startIndex, int count)
        {
            byte maxR = 0, maxG = 0, maxB = 0, maxA = 0;
            var ptr = (byte*)Pixels.GetUnsafeReadOnlyPtr();

            for (int i = 0; i < count; i++)
            {
                int baseOffset = (startIndex + i) * BytesPerPixel;

                if (ChannelCount > 0)
                {
                    byte v = ptr[baseOffset + RChannelOffset];
                    if (v > maxR) maxR = v;
                }
                if (ChannelCount > 1)
                {
                    byte v = ptr[baseOffset + GChannelOffset];
                    if (v > maxG) maxG = v;
                }
                if (ChannelCount > 2)
                {
                    byte v = ptr[baseOffset + BChannelOffset];
                    if (v > maxB) maxB = v;
                }
                if (ChannelCount > 3)
                {
                    byte v = ptr[baseOffset + AChannelOffset];
                    if (v > maxA) maxA = v;
                }
            }

            // Batches from ScheduleBatch start at consecutive multiples of BatchSize (only the final
            // batch may be shorter), so startIndex / BatchSize is a stable, unique batch index.
            int batchIndex = startIndex / BatchSize;
            MaxPerBatchR[batchIndex] = maxR;
            MaxPerBatchG[batchIndex] = maxG;
            MaxPerBatchB[batchIndex] = maxB;
            MaxPerBatchA[batchIndex] = maxA;
        }
    }
}
