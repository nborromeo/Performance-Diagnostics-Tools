using System.Collections.Generic;
using System.Linq;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>A blockHeight x blockWidth window of adjacent, present TerrainGridCells, row-major (Cells[row, col]).</summary>
    public sealed class TerrainBlock
    {
        public readonly TerrainGridCell[,] Cells;
        public readonly int Height;
        public readonly int Width;

        public TerrainBlock(TerrainGridCell[,] cells)
        {
            Cells = cells;
            Height = cells.GetLength(0);
            Width = cells.GetLength(1);
        }
    }

    /// <summary>
    /// Splits a discovered TerrainGrid into non-overlapping blockWidth x blockHeight windows.
    /// A window is only emitted if every cell inside it is present in the grid — any window with a
    /// missing cell (a hole, or a grid remainder smaller than the block size) is left as a leftover
    /// rather than merged as a smaller block.
    /// </summary>
    public static class TerrainBlockChunker
    {
        public static List<TerrainBlock> ChunkGrid(TerrainGrid grid, int blockWidth, int blockHeight, out List<TerrainGridCell> leftovers)
        {
            var blocks = new List<TerrainBlock>();
            var consumed = new HashSet<TerrainGridCell>();

            if (grid.Cells.Count == 0)
            {
                leftovers = new List<TerrainGridCell>();
                return blocks;
            }

            int minRow = grid.Cells.Min(c => c.Row);
            int maxRow = grid.Cells.Max(c => c.Row);
            int minCol = grid.Cells.Min(c => c.Col);
            int maxCol = grid.Cells.Max(c => c.Col);

            for (int row = minRow; row + blockHeight - 1 <= maxRow; row += blockHeight)
            {
                for (int col = minCol; col + blockWidth - 1 <= maxCol; col += blockWidth)
                {
                    var cells = new TerrainGridCell[blockHeight, blockWidth];
                    bool full = true;

                    for (int dr = 0; dr < blockHeight && full; dr++)
                    {
                        for (int dc = 0; dc < blockWidth && full; dc++)
                        {
                            if (!grid.ByCoord.TryGetValue((row + dr, col + dc), out var cell))
                            {
                                full = false;
                                break;
                            }
                            cells[dr, dc] = cell;
                        }
                    }

                    if (!full)
                        continue;

                    blocks.Add(new TerrainBlock(cells));
                    foreach (var cell in cells)
                        consumed.Add(cell);
                }
            }

            leftovers = grid.Cells.Where(c => !consumed.Contains(c)).ToList();
            return blocks;
        }
    }
}
