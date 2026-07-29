# Scene Build Optimizer

<img width="649" height="686" alt="image" src="https://github.com/user-attachments/assets/1dc8a0f1-0cc0-49f2-85b9-be0fc838debe" />

Generates optimized copies of scenes ahead of a build, without ever modifying the authored scenes or assets they reference. Add scenes to the window's tracked list, click Optimize, and use the generated scene (instead of the authoring one) in your Build Settings or Build Profile.

**Open:** `Window > Analysis > Scene Build Optimizer`

## Why a separate optimized scene, not a build-time transform?

An earlier design hooked `IProcessSceneWithReport` to transform the scene transiently during a build. Two things ruled that out:

- Its own docs state the callback "doesn't support modifying the state of other assets" — only the scene itself. Optimizing a terrain means mutating its `TerrainData`, a separate asset.
- Unity can skip the callback entirely on an incremental build "if the scene or related content in the project is unchanged from the previous Player build," and it's documented as unreliable under the Scriptable Build Pipeline that Addressables uses.

So instead, an optimized scene is a real, persistent asset you generate on demand (or that gets auto-refreshed before a build if stale) — from the build's perspective it's just an ordinary scene, so there's no reliability gap and no restricted-callback problem.

## Window layout

- **Scenes** — drag a `SceneAsset` into the field (or click "Add Active Scene") to track it. Each row shows whether its optimized copy is *Not generated*, *Stale* (source changed since last generation), or *Up to date*, plus buttons to (re)generate it or ping the generated asset.
- **Optimizers** — one row per registered optimizer, with an enable toggle and a ⚙ settings popup.
- **Last Run** — the report from the most recent Optimize/Refresh: what changed, and any warnings.

## Generating an optimized scene

For `Assets/Scenes/Level01.unity`, the optimized copy is generated at `Assets/Scenes/Optimized/Level01/Level01.unity` by default. Generation:

1. Copies the source `.unity` file to that path (source untouched).
2. Opens the copy and runs every enabled optimizer against it.
3. Saves the copy and a `Level01.optimized.json` manifest recording the source scene's content hash plus every asset the optimizers duplicated (and their hashes), so future runs can tell "nothing changed" from "regenerate this."

A pre-build check (`IPreprocessBuildWithReport`) automatically regenerates any optimized scene in the active Build Profile (or global Build Settings) whose manifest reports it's stale, so you can't accidentally ship an out-of-date optimized scene.

## Per-Build-Profile settings

Unity 6's `BuildProfile` has no public extension point for third-party override sections, so this tool keeps its own settings asset (registered via `EditorBuildSettings`, like Addressables/NavMesh do) with a default configuration plus optional per-`BuildProfile` overrides, resolved against `BuildProfile.GetActiveBuildProfile()` at generation time.

## Optimizer execution order

Optimizers can mutate the same underlying assets as each other, so the order they run in matters. `ISceneOptimizer.Order` (lower runs first, ties broken by registration order) makes that order explicit rather than leaving it to whatever order optimizers happen to register in — `SceneOptimizerRegistry` keeps its list sorted by `Order` at all times, so the window's optimizer list and the generator's execution order always agree.

Currently: **Terrain Layer Optimizer** (`Order = 100`) runs before **Terrain Tile Merger** (`Order = 200`) — stripping each tile's unused splat layers first can let more tile blocks satisfy the merger's draw-call-pass budget (see below), so running layer stripping first strictly helps, never hurts.

## Included optimizer: Terrain Layer Optimizer

Finds every `Terrain` in the scene and, for each one's `TerrainData`, scans every alphamap pixel to determine which `TerrainLayer`s never exceed a configurable weight threshold ("Weight epsilon", default 0 = exactly zero everywhere) anywhere on the terrain. If any layers are unused:

1. The TerrainData is duplicated into an `OptimizedTerrainData/` subfolder next to the optimized scene (source `TerrainData` asset untouched).
2. Unused layers are removed and the alphamap textures are repacked to match.
3. Every `Terrain`/`TerrainCollider` in the optimized scene that used the original TerrainData is repointed at the copy.

**Detection performance:** rather than `TerrainData.GetAlphamaps` (a full managed `float[width, height, layerCount]` copy — ~1.3GB for a 2049² 8-layer terrain), detection reads the alphamap texture's raw native memory directly (`Texture2D.GetRawTextureData<byte>()`) and scans it with a Burst-compiled `IJobParallelForBatch`, resolving each texture's actual R/G/B/A byte order from its `GraphicsFormat` rather than assuming one.

**Shared TerrainData:** if a terrain's `TerrainData` is referenced by anything other than the scene being optimized (another scene, a prefab), it's left untouched and a warning is logged instead of silently duplicating a wider dependency chain.

## Included optimizer: Terrain Tile Merger

Merges NxN grids of adjacent `Terrain` tiles into fewer, larger `Terrain`/`TerrainData` assets — e.g. a 4x4 grid of tiles merged in 2x2 blocks becomes a 2x2 grid of tiles, each the combination of a 2x2 block of the originals — with no seam introduced in the merged heightmap or splatmap.

**Settings:** block width and height (tiles merged per block along each axis). The two must be equal — see "Square blocks only" below.

### Finding the grid

Adjacency is detected from each tile's world-space footprint (`transform.position` + `terrainData.size`), not from Unity's own `Terrain.leftNeighbor`/`rightNeighbor`/`topNeighbor`/`bottomNeighbor`. Those links aren't persisted scene data — Unity's native "auto-connect" system computes them lazily (only once a terrain has actually rendered a frame), so they're unreliably still `null` on a scene that was just duplicated/loaded by script, which is exactly the context this optimizer runs in.

The grid is chunked into non-overlapping `BlockWidth`x`BlockHeight` windows. A window only merges if every tile inside it is present (no holes) — any grid remainder that doesn't fill a whole block, or a block that fails validation (below), is left unmerged rather than folded into a smaller block.

### What must match to merge a block, and what doesn't

To avoid resampling (which would reintroduce the exact seam risk this optimizer exists to avoid), every tile in a block must have **identical**:

- `heightmapResolution`, and the block size must be square (`BlockWidth == BlockHeight`) — Unity terrain heightmaps, alphamaps, and detail maps are always square textures, and `heightmapResolution` is clamped to one of `33, 65, 129, 257, 513, 1025, 2049, 4097`. A block whose merged resolution `(tileRes-1)*N+1` wouldn't land on one of those values is skipped rather than let Unity silently resample/clamp it.
- `size`, `alphamapResolution`, `alphamapWidth`/`alphamapHeight`, `detailResolution`, `detailResolutionPerPatch`.

Tiles are **not** required to share the same splat layers, tree prototypes, or detail prototypes — those get merged into a union instead:

- **Splat layers**: the block's tiles can use different `TerrainLayer` sets, but only if merging them doesn't need *more* alphamap draw-call passes (4 layers per pass) than the costliest tile in the block already needed alone. Two 2-layer tiles merge fine (1 pass either way); a 5-layer tile absorbing a 2-layer tile is fine too (already 2 passes, still 2 after); two 3-layer tiles do not merge (1 pass each alone, 2 once combined) — merging would make rendering worse, not better. Each tile's alphamap weights are remapped onto the union list's indices; a merged layer a tile doesn't paint at all gets weight 0 in that tile's texels.
- **Trees and detail meshes**: prototype lists are unioned freely (no draw-call-pass budget applies to these), deduplicated by prefab (trees) or prototype/prototypeTexture (details). Each tile's tree instances and detail layer data are remapped from its own local prototype indices onto the merged list.

### Stitching the result

Unity's terrain neighbor-link stitching (what removes LOD/skirt cracks at tile boundaries) is driven entirely by its native auto-connect system on every scene load — there's no serialized neighbor field to write into directly. Auto-connect links any terrains sharing a `groupingID` whose `terrainData.size` matches *exactly*. Merged tiles from one generation pass all end up the same size as each other, so they keep the template tile's `groupingID`/`allowAutoConnect` shifted by a fixed offset, isolating them into their own uniform auto-connect group separate from any leftover, still-original-size tiles — letting Unity's own compliant stitching handle merged-to-merged boundaries correctly on every load, the same way it already does for an untouched, uniform grid.

**Known limitation:** a merged tile bordering a leftover (unmerged) tile is a genuine size mismatch that Unity's auto-connect will never link (by its own size-matching rule), and there's no persisted field a script can write a manual link into instead — that boundary may show a visible seam. This is a Terrain engine limitation rather than something fixable from script without a much larger change (e.g. a runtime component that reapplies `Terrain.SetNeighbors` on scene load via serialized `GameObject` references, which do persist).

**Known limitation — detail/grass placement isn't deterministic across a merge:** unlike tree instances (explicit positions, preserved exactly), Unity scatters individual detail/grass instances procedurally from the density map at render time, seeded by the `TerrainData`/`Terrain` instance itself. A merge always creates a new instance, so the exact sub-cell scatter pattern shifts even though the underlying density data is merged and preserved correctly — expect matching coverage/density, not pixel-identical blade placement.

**Manifest/referrer handling:** every source `TerrainData` a block consumes is checked for outside referrers exactly like the Terrain Layer Optimizer does (skip + warn rather than duplicate a wider dependency chain), and gets recorded in the scene's manifest via `report.LogCopiedAsset` so staleness detection still works even though several source assets collapse into one merged asset.
