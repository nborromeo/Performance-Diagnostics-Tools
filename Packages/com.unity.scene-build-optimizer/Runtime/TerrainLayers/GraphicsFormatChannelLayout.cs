using System;
using UnityEngine.Experimental.Rendering;

namespace SceneBuildOptimizer.TerrainLayers
{
    /// <summary>Byte offset of each semantic channel (R,G,B,A) within one pixel of a given format.</summary>
    public readonly struct ChannelByteOffsets
    {
        public readonly int R, G, B, A;

        public ChannelByteOffsets(int r, int g, int b, int a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
    }

    /// <summary>
    /// Resolves per-channel byte offsets for 8-bit-per-channel RGBA formats.
    ///
    /// This matters for correctness: <see cref="GraphicsFormat"/> names document memory byte order
    /// directly (e.g. "R8G8B8A8" = R at byte 0, "B8G8R8A8" = B at byte 0), and terrain alphamap
    /// textures aren't guaranteed to be in plain R,G,B,A order. Reading raw texture bytes without
    /// resolving this per-format would silently mislabel which TerrainLayer a channel belongs to.
    /// </summary>
    public static class GraphicsFormatChannelLayout
    {
        public static ChannelByteOffsets GetByteOffsets(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_SRGB:
                    return new ChannelByteOffsets(0, 1, 2, 3);

                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_SRGB:
                    return new ChannelByteOffsets(2, 1, 0, 3);

                default:
                    throw new NotSupportedException(
                        $"Scene Build Optimizer: terrain alphamap texture uses unsupported GraphicsFormat '{format}' " +
                        "(expected an 8-bit-per-channel RGBA/BGRA format). Add support in GraphicsFormatChannelLayout " +
                        "rather than assuming a channel order.");
            }
        }
    }
}
