# Scene Build Optimizer

Generates optimized copies of scenes ahead of a build, without ever modifying the authored scenes or assets. Each registered optimizer runs against a duplicated scene (and duplicates of any assets it needs to mutate), producing a persistent "optimized" scene asset you add to your Build Settings / Build Profile instead of the authoring scene.

**Open:** `Window > Analysis > Scene Build Optimizer`

📄 [Full documentation](../../docs/scene-build-optimizer.md)

## Included optimizers

Optimizers run in a fixed, explicit order (`ISceneOptimizer.Order`, lower first) rather than whatever order they happen to register in:

1. **Terrain Layer Optimizer** — scans every terrain in a scene and removes `TerrainLayer`s whose alphamap weight is zero (or below a configurable epsilon) everywhere on the terrain, then repacks the remaining layers. Detection uses a Burst job reading raw alphamap texture memory directly, so large/many terrains scan without paying for a full managed alphamap copy.
2. **Terrain Tile Merger** — merges NxN grids of adjacent Terrain tiles into fewer, larger Terrain/TerrainData assets, with no seam in the merged heightmap or splatmap. Runs *after* the Layer Optimizer on purpose: stripping each tile's unused layers first can let more blocks satisfy the merger's draw-call-pass budget (see full docs above).
