# Scene Build Optimizer

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

## Included optimizer: Terrain Layer Optimizer

Finds every `Terrain` in the scene and, for each one's `TerrainData`, scans every alphamap pixel to determine which `TerrainLayer`s never exceed a configurable weight threshold ("Weight epsilon", default 0 = exactly zero everywhere) anywhere on the terrain. If any layers are unused:

1. The TerrainData is duplicated into an `OptimizedTerrainData/` subfolder next to the optimized scene (source `TerrainData` asset untouched).
2. Unused layers are removed and the alphamap textures are repacked to match.
3. Every `Terrain`/`TerrainCollider` in the optimized scene that used the original TerrainData is repointed at the copy.

**Detection performance:** rather than `TerrainData.GetAlphamaps` (a full managed `float[width, height, layerCount]` copy — ~1.3GB for a 2049² 8-layer terrain), detection reads the alphamap texture's raw native memory directly (`Texture2D.GetRawTextureData<byte>()`) and scans it with a Burst-compiled `IJobParallelForBatch`, resolving each texture's actual R/G/B/A byte order from its `GraphicsFormat` rather than assuming one.

**Shared TerrainData:** if a terrain's `TerrainData` is referenced by anything other than the scene being optimized (another scene, a prefab), it's left untouched and a warning is logged instead of silently duplicating a wider dependency chain.
