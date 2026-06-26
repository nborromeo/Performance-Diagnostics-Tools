# Unity Performance Diagnostics Tools

A collection of Unity Editor tools for diagnosing rendering and physics performance. All tools are embedded packages that work in Unity 6000.0 and later.

---

## Runtime Project Auditor (aka Performance Diagnostics)

<img width="1336" height="456" alt="image" src="https://github.com/user-attachments/assets/7a57c3ef-4553-4b46-b09b-163ebb6733d0" />

A unified window that runs multiple diagnostic detectors simultaneously and collects all findings into a single, sortable list. Each detector can be toggled and configured independently from the toolbar.

**Open:** `Window > Analysis > Performance Diagnostics`

### Window layout

The toolbar at the top contains a section for each active detector — with its category toggle, primary action controls, and a settings (⚙) popup. The total issue count is shown on the right. Below the toolbar a split view shows the issue list on the left and a details panel on the right.

The issue list has the following columns:

| Column | Meaning |
|--------|---------|
| **Type** | Which detector produced the entry, and the specific event type within that detector |
| **Frame** | The frame number when the issue was captured |
| **Object** | The name of the GameObject involved |
| **Context** | Additional context — depends on the detector (e.g. Canvas name, break reason) |
| **Count** | How many times the same issue was recorded (repeated events fold into one row) |

Rows with a yellow background were captured in Play Mode; blue background rows were captured in Edit Mode. Click any row to open the details panel on the right.

---

### Canvas Invalidation detector

Logs every call that adds an element to the Canvas layout or graphic rebuild queues, capturing the full call stack at the moment of invalidation. Use this to find what code is causing unnecessary rebuilds every frame.

#### How it works

The detector patches methods at the native code level using a 14-byte JMP detour (Windows, macOS, and Linux Editor builds are supported). When any patched method is called, a hook captures the current stack trace and stores it.

**CanvasUpdateRegistry** — patched to catch managed layout and graphic rebuild registrations:
- `RegisterCanvasElementForLayoutRebuild`
- `TryRegisterCanvasElementForLayoutRebuild`
- `RegisterCanvasElementForGraphicRebuild`
- `TryRegisterCanvasElementForGraphicRebuild`

**CanvasRenderer** — patched to catch native setter calls that bypass `CanvasUpdateRegistry` entirely (e.g. `SetColor`, `SetMesh`). These appear as **CR** (orange) entries in the list. CanvasRenderer events that are a downstream consequence of a managed Graphic/Layout invalidation already captured in the same frame are suppressed automatically to avoid duplicate noise.

**Graphic tween methods** — `CrossFadeColor` (all overloads) and `CrossFadeAlpha` are patched to attribute per-frame tween updates to their originating call site rather than to the tween engine internals.

The **Traces ON / Traces OFF** indicator in the toolbar shows whether patching succeeded. If it shows OFF, entries are still captured but without call stacks.

#### Toolbar controls

| Control | Description |
|---------|-------------|
| **Canvas Invalidation toggle** | Show or hide Canvas Invalidation entries in the list |
| **Clear** | Removes all captured Canvas Invalidation entries |
| **Pause / Resume** | Temporarily stops capturing new entries |
| **Layout / Graphic / CR** | Toggle filters for each invalidation sub-type |
| **Max** | Maximum number of entries to keep (oldest are trimmed) |
| **Traces ON/OFF** | Green = patching active, Orange = patching inactive |

#### Details panel

Selecting a Canvas Invalidation entry opens its details:

- **Object** — full hierarchy path, with Ping and Select buttons to locate it in the scene
- **Invalidation Details** — type, frame, time, mode, and dirty flags
- **Canvas** — Canvas name and render mode
- **Components** — all components on the GameObject at capture time
- **Call-site Stack Trace** — the full managed call stack from the moment the element was queued for rebuild. Stack frames that resolve to a source file are rendered as clickable links — clicking opens the file at the correct line in your script editor. When multiple unique call stacks produced the same invalidation (folded rows), use the **◀ ▶** arrows to page through each distinct trace. Use **Copy to Clipboard** to paste the current trace into an editor or bug report.

#### Common findings

- A stack trace showing `Graphic.SetVerticesDirty` or `Graphic.SetMaterialDirty` called from an `Update` or animation callback every frame means a graphic is being dirtied continuously — the most common cause of constant rebuilds.
- Layout invalidations triggered by `LayoutRebuilder.MarkLayoutForRebuild` on stable objects usually point to a script calling `SetActive`, changing a `RectTransform`, or modifying layout component properties unnecessarily.
- If many objects share the same stack trace, fix it once at the call site to eliminate all of them.
- **CR entries with a high Count** indicate a CanvasRenderer property (color, mesh, etc.) being set every frame from script. These bypass the managed rebuild path and won't appear as Layout or Graphic entries, making them easy to miss without this tool.
- **Multiple unique traces on a single folded row** (shown via the ◀ ▶ pager) means the same object is being invalidated by more than one code path in the same frame — each trace is a separate fix target.

---

### Static Rebuild detector

Detects static colliders — GameObjects with a `Collider` but no `Rigidbody` in their parent chain — that are causing physics broadphase rebuilds. Any of the following events on a static collider forces Unity to rebuild the broadphase and tank physics performance:

- The GameObject moved, rotated, or scaled
- The GameObject was activated or deactivated
- A `Collider` component was added or removed
- A `Collider` component was enabled or disabled
- The GameObject was created or destroyed

#### How it works

The detector snapshots the world transform and collider state of every static collider GO in the scene, waits a configurable interval, then diffs the two snapshots. Only GOs that have a `Collider` somewhere in their subtree and no `Rigidbody` anywhere in their parent chain are considered.

#### Toolbar controls

| Control | Description |
|---------|-------------|
| **Static Rebuild toggle** | Show or hide Static Rebuild entries in the list |
| **Interval (s)** | Seconds between the two snapshots (0.05 – 5 s) |
| **Continuous** | When enabled, keeps re-snapshotting and updating results live; **Start / Stop** replaces **Capture** |
| **Limit Iterations** | Only available in Continuous mode. Caps the number of captures before auto-stopping |
| **Max Iterations** | How many captures to run before stopping (1 – 1000) |
| **Capture / Start** | Takes the first snapshot and begins waiting |
| **Stop** | Stops a running continuous capture; shows current progress as `Stop (N / Max)` |
| **Clear** | Removes all accumulated Static Rebuild results |

#### Reading the results

Results appear in the shared issue list — one row per unique event per GameObject, accumulating across captures. Click any row to ping and select the object in the Hierarchy. Destroyed GameObjects appear in grey and cannot be selected.

The **Type** column shows the specific event that was detected:

| Type value | Meaning |
|-----------|---------|
| **Move** | Transform change detected (position, rotation, or scale) |
| **C+** | Collider component was added |
| **C-** | Collider component was destroyed |
| **C▲** | Collider component was enabled |
| **C▼** | Collider component was disabled |
| **Act** | `activeInHierarchy` flipped from false → true |
| **Deact** | `activeInHierarchy` flipped from true → false |
| **Born** | GO did not exist in the previous snapshot (was spawned) |
| **Dead** | GO was present in the previous snapshot but is now gone (was destroyed) |

All column headers and cells show a tooltip with a plain-language description on hover.

---

### Requirements

- Unity 6000.0 or later
- Canvas Invalidation detector: `com.unity.ugui` 2.0.0 or later; native patching requires Windows x64, macOS x64/arm64, or Linux x64 Editor (entries are still captured without stack traces on other platforms)
- Static Rebuild detector: must be used in **Play Mode** — transforms must be live

---

## UI Batch Highlighter

<img width="921" height="793" alt="image" src="https://github.com/user-attachments/assets/1111084c-1550-41b6-83d1-bfa48036ef22" />

Visualizes every draw batch produced by a Canvas directly in the Scene and Game views, highlighting the first and last element of each batch so you can quickly identify what is causing batch breaks.

**Open:** `Window > Analysis > UI Batch Highlighter`

### How it works

The tool captures a single frame of UI Profiler data and maps each batch's element list back to GameObjects in the scene. It then draws colored overlays around the RectTransforms of those elements:

| Color | Meaning |
|-------|---------|
| Green | First element in the batch |
| Red | Last element in the batch |
| Yellow | All other elements in the batch |
| Cyan | Stencil push (Mask start) |
| Magenta | Stencil pop (Mask end) |

### Workflow

1. Open the tool window.
2. Make sure the Profiler is not already recording — the tool manages recording state automatically.
3. Click **Capture Frame**. The tool enables the Profiler for one frame, reads the UI batch data, then stops recording.
4. The batch list appears in the window grouped by Canvas. Each batch entry shows its type, break reason, stencil depth, and element count.
5. Click any element button to ping and select it in the Hierarchy.
6. Click **Highlight Batch** to select all elements in that batch at once.
7. Click **Clear Highlights** to remove the overlays.

### Reading the results

The **break reason** shown on each batch tells you why a new batch had to start (e.g. texture change, material change, stencil depth change). Elements at the boundary — the last element of one batch and the first of the next — are the most likely culprits. Clicking those buttons and inspecting their materials, textures, and mask depth is usually enough to find the issue.

Stencil push/pop entries represent `Mask` components. Each nested mask adds one level of depth and forces a batch break, so reducing mask nesting directly reduces batch count.

### Requirements

- Unity 6000.3 or later
- The UI Profiler must be available (`com.unity.ugui` installed)
- Overlays are drawn via `SceneView.duringSceneGui` and `Camera.onPostRender`; both Scene and Game view must be visible for overlays to appear in both

---

## Shader Variant Analyzer

<img width="1080" height="492" alt="image" src="https://github.com/user-attachments/assets/9a360136-a1af-402e-9fb4-35f1055f8735" />

Analyzes a shader's keyword declarations and the materials in the project that reference it, giving you a clear picture of how many shader variants are being compiled and what is driving that count.

**Open:** `Window > Analysis > Shader Variant Analyzer`

### Workflow

1. Open the tool window.
2. Drag a shader asset into the **Shader** field in the toolbar (or use the object picker).
3. Click **Analyze**. The tool parses the shader's source files (including resolved `#include` chains) and scans all materials in the project.
4. Use the three tabs to explore the results.

### Tab 0 — Shader Feature Keywords

Lists every keyword declared with `#pragma shader_feature` (or `shader_feature_local`) found in the shader source. These keywords are per-material — only the keywords enabled on materials that actually reference this shader generate variants.

| Column | Meaning |
|--------|---------|
| **Keyword** | The keyword name, as it appears in the pragma |
| **Permutations** | How many compiled permutations include this keyword in the enabled state |
| **Materials** | How many project materials have this keyword enabled |

Clicking a keyword row expands a detail panel listing every material that has the keyword enabled, with ping/select buttons. The **Keyword** column also shows the source file and line number where the `#pragma` was found — clicking that link opens the file at that line in your script editor. Built-in keywords not found in parsed source are marked accordingly.

Click any column header to sort by that field.

### Tab 1 — Multi-Compile Keywords

Lists every `#pragma multi_compile` (or `multi_compile_local`) set found in the shader source. Unlike `shader_feature`, `multi_compile` keywords are always compiled in full regardless of which materials exist, so a single set with many options has a large multiplying effect on total variant count.

| Column | Meaning |
|--------|---------|
| **Keyword set** | All options in the pragma, e.g. `FOG_ON \| FOG_EXP2` |
| **Options** | The number of options in the set (each option multiplies the total variant count) |

Built-in keyword sets (those not found in parsed source files) are flagged separately. The source file and line number are shown for sets that come from project or package source. Click any column header to sort.

### Tab 2 — Permutations

Lists every unique permutation that exists across all materials in the project referencing this shader. Each row represents one unique combination of enabled `shader_feature` keywords found on at least one material.

| Column | Meaning |
|--------|---------|
| **Active Shader Feature Keywords** | The set of enabled keywords that defines this permutation. Rows with no enabled keywords are labeled `(base — no shader_feature keywords)` |
| **Materials** | How many project materials use exactly this permutation |

Selecting a row expands a detail panel listing every material in that permutation, with ping/select buttons. This tab is the fastest way to identify permutations shared across many materials (good consolidation candidates) and permutations used by only one material (potential dead weight if the variant is rarely seen at runtime).

### Requirements

- Unity 6000.0 or later
- The shader must be a project asset (not a built-in shader) for source parsing and material scanning to work
