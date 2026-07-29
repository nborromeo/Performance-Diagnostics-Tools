using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SceneBuildOptimizer.TerrainTileMerger
{
    /// <summary>A single Terrain tile discovered as part of a connected grid, with its row/col position relative to an arbitrary origin tile in that grid.</summary>
    public sealed class TerrainGridCell
    {
        public readonly Terrain Terrain;
        public readonly int Row;
        public readonly int Col;

        public TerrainGridCell(Terrain terrain, int row, int col)
        {
            Terrain = terrain;
            Row = row;
            Col = col;
        }
    }

    /// <summary>A set of Terrain tiles whose world-space footprints form a contiguous grid.</summary>
    public sealed class TerrainGrid
    {
        public readonly List<TerrainGridCell> Cells;
        public readonly Dictionary<(int row, int col), TerrainGridCell> ByCoord;

        public TerrainGrid(List<TerrainGridCell> cells, Dictionary<(int row, int col), TerrainGridCell> byCoord)
        {
            Cells = cells;
            ByCoord = byCoord;
        }
    }

    /// <summary>
    /// Discovers connected grids of Terrain tiles from their world-space footprints
    /// (transform.position + terrainData.size), rather than Terrain's own
    /// leftNeighbor/rightNeighbor/topNeighbor/bottomNeighbor links: those are computed lazily by
    /// Unity's terrain auto-connect system (only once a terrain has actually rendered a frame), so
    /// they're reliably still null on a scene that was just duplicated/loaded by script — which is
    /// exactly the context this optimizer runs in. Position/size adjacency is available
    /// synchronously and gives the same result whenever tiles are genuinely edge-to-edge.
    ///
    /// Row/col coordinates are derived from ABSOLUTE position relative to one shared origin per
    /// connected component — not by accumulating +1/-1 steps during the flood-fill. Accumulating
    /// relative offsets is fragile: any single inconsistent adjacency test along the way (float
    /// tolerance, a slightly-off tile) lets two different tiles drift onto the same computed
    /// coordinate, silently colliding in the lookup table and corrupting both block chunking and
    /// neighbor relinking for the rest of that grid.
    /// </summary>
    public static class TerrainGridDiscovery
    {
        /// <summary>Fraction of a tile's footprint used as position/size tolerance when matching edges, to absorb float imprecision without treating genuinely different tile sizes as adjacent.</summary>
        const float RelativeEpsilon = 0.001f;

        public static List<TerrainGrid> DiscoverGrids(IReadOnlyList<Terrain> allTerrains)
        {
            var footprints = new Dictionary<Terrain, Footprint>();
            foreach (var terrain in allTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;
                footprints[terrain] = Footprint.Of(terrain);
            }

            var visited = new HashSet<Terrain>();
            var grids = new List<TerrainGrid>();

            foreach (var seed in footprints.Keys)
            {
                if (visited.Contains(seed))
                    continue;

                var component = FloodFillComponent(seed, footprints, visited);
                grids.Add(BuildGrid(component, footprints));
            }

            return grids;
        }

        /// <summary>Finds every tile connected to <paramref name="seed"/> via edge-to-edge adjacency. Only used for connectivity — not for coordinate assignment, see class remarks.</summary>
        static List<Terrain> FloodFillComponent(Terrain seed, Dictionary<Terrain, Footprint> footprints, HashSet<Terrain> visited)
        {
            var component = new List<Terrain>();
            var queue = new Queue<Terrain>();

            visited.Add(seed);
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var terrain = queue.Dequeue();
                component.Add(terrain);
                var footprint = footprints[terrain];

                foreach (var candidate in footprints)
                {
                    if (visited.Contains(candidate.Key))
                        continue;
                    if (!footprint.IsAdjacent(candidate.Value))
                        continue;

                    visited.Add(candidate.Key);
                    queue.Enqueue(candidate.Key);
                }
            }

            return component;
        }

        /// <summary>
        /// Assigns row/col coordinates to a connected component by clustering the tiles' actual
        /// MinX/MinZ positions into columns/rows, rather than dividing distance-from-origin by one
        /// arbitrarily-picked tile's size. Dividing by a single reference size compounds any
        /// per-tile size/position inconsistency the farther a tile is from the origin — which would
        /// systematically bite exactly the far corner of a grid and nowhere else. Clustering instead
        /// only requires that tiles sharing a column/row actually share a MinX/MinZ (within
        /// tolerance), with no assumption that every tile is exactly the same size.
        /// </summary>
        static TerrainGrid BuildGrid(List<Terrain> component, Dictionary<Terrain, Footprint> footprints)
        {
            float minTileExtent = component.Min(t =>
            {
                var footprint = footprints[t];
                return Mathf.Min(footprint.MaxX - footprint.MinX, footprint.MaxZ - footprint.MinZ);
            });
            float tolerance = minTileExtent * RelativeEpsilon;

            var columns = BuildClusters(component.Select(t => footprints[t].MinX), tolerance);
            var rows = BuildClusters(component.Select(t => footprints[t].MinZ), tolerance);

            var cells = new List<TerrainGridCell>(component.Count);
            var byCoord = new Dictionary<(int, int), TerrainGridCell>(component.Count);

            foreach (var terrain in component)
            {
                var footprint = footprints[terrain];
                int col = IndexOfCluster(columns, footprint.MinX, tolerance);
                int row = IndexOfCluster(rows, footprint.MinZ, tolerance);
                var coord = (row, col);

                if (byCoord.TryGetValue(coord, out var existing))
                {
                    Debug.LogWarning($"Scene Build Optimizer: Terrain Tile Merger — '{terrain.name}' and '{existing.Terrain.name}' both resolved to grid coordinate {coord}, likely due to inconsistent tile sizes in this grid. '{terrain.name}' will be left untouched rather than risk corrupting the merge.");
                    continue;
                }

                var cell = new TerrainGridCell(terrain, row, col);
                cells.Add(cell);
                byCoord[coord] = cell;
            }

            return new TerrainGrid(cells, byCoord);
        }

        /// <summary>Sorts <paramref name="values"/> and collapses runs within <paramref name="tolerance"/> of each other into single cluster positions, in ascending order.</summary>
        static List<float> BuildClusters(IEnumerable<float> values, float tolerance)
        {
            var clusters = new List<float>();
            foreach (var value in values.OrderBy(v => v))
            {
                if (clusters.Count == 0 || value - clusters[clusters.Count - 1] > tolerance)
                    clusters.Add(value);
            }
            return clusters;
        }

        static int IndexOfCluster(List<float> clusters, float value, float tolerance)
        {
            for (int i = 0; i < clusters.Count; i++)
            {
                if (Mathf.Abs(value - clusters[i]) <= tolerance)
                    return i;
            }
            return clusters.Count - 1; // shouldn't happen — value came from the same set the clusters were built from
        }

        readonly struct Footprint
        {
            public readonly float MinX, MaxX, MinZ, MaxZ;

            Footprint(float minX, float maxX, float minZ, float maxZ)
            {
                MinX = minX;
                MaxX = maxX;
                MinZ = minZ;
                MaxZ = maxZ;
            }

            public static Footprint Of(Terrain terrain)
            {
                var position = terrain.transform.position;
                var size = terrain.terrainData.size;
                return new Footprint(position.x, position.x + size.x, position.z, position.z + size.z);
            }

            /// <summary>Whether this footprint and <paramref name="other"/> share a full edge (aligned corners, matching edge length) on any of the four sides.</summary>
            public bool IsAdjacent(Footprint other)
            {
                float tolerance = RelativeEpsilon * Mathf.Max(MaxX - MinX, MaxZ - MinZ, 1f);

                bool sameZExtent = Mathf.Abs(MinZ - other.MinZ) < tolerance && Mathf.Abs(MaxZ - other.MaxZ) < tolerance;
                bool sameXExtent = Mathf.Abs(MinX - other.MinX) < tolerance && Mathf.Abs(MaxX - other.MaxX) < tolerance;

                if (sameZExtent && (Mathf.Abs(MaxX - other.MinX) < tolerance || Mathf.Abs(MinX - other.MaxX) < tolerance))
                    return true;
                if (sameXExtent && (Mathf.Abs(MaxZ - other.MinZ) < tolerance || Mathf.Abs(MinZ - other.MaxZ) < tolerance))
                    return true;

                return false;
            }
        }
    }
}
