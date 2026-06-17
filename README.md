# Unity Performance Diagnostics Tools

A collection of Unity Editor tools for diagnosing rendering and physics performance. All tools are embedded packages that work in Unity 6000.0 and later.

---

## Canvas Invalidation Tracker

<img width="1197" height="819" alt="image" src="https://github.com/user-attachments/assets/f78e6cbc-2e3d-47e3-9b94-3aef1192b6fc" />

Logs every call that adds an element to the Canvas layout or graphic rebuild queues, capturing the full call stack at the moment of invalidation. Use this tool to find out what code is causing unnecessary rebuilds every frame.

**Open:** `Window > Analysis > Canvas Invalidation Tracker`

### How it works

The tool patches methods at the native code level using a 14-byte JMP detour (Windows, macOS, and Linux Editor builds are supported). When any patched method is called, a hook captures the current stack trace and stores it.

**CanvasUpdateRegistry** — patched to catch managed layout and graphic rebuild registrations:
- `RegisterCanvasElementForLayoutRebuild`
- `TryRegisterCanvasElementForLayoutRebuild`
- `RegisterCanvasElementForGraphicRebuild`
- `TryRegisterCanvasElementForGraphicRebuild`

**CanvasRenderer** — patched to catch native setter calls that bypass `CanvasUpdateRegistry` entirely (e.g. `SetColor`, `SetMesh`). These appear as **CR** (orange) entries in the list. CanvasRenderer events that are a downstream consequence of a managed Graphic/Layout invalidation already captured in the same frame are suppressed automatically to avoid duplicate noise.

**Graphic tween methods** — `CrossFadeColor` (all overloads) and `CrossFadeAlpha` are patched to attribute per-frame tween updates to their originating call site rather than to the tween engine internals.

The **Traces ON / Traces OFF** indicator in the toolbar shows whether patching succeeded. If it shows OFF, entries are still captured but without call stacks.

### Toolbar controls

| Control | Description |
|---------|-------------|
| **Clear** | Removes all captured entries |
| **Pause / Resume** | Temporarily stops capturing new entries |
| **Layout / Graphic / CR** | Toggle filters for each invalidation type |
| **Max** | Maximum number of entries to keep (oldest are trimmed) |
| **Traces ON/OFF** | Green = patching active, Orange = patching inactive |

### Reading the entry list

The list uses sortable, resizable columns — click any column header to sort by that field. The columns are:

- **Type badge** — `LAYOUT` (blue), `GRAPHIC` (green), or the CanvasRenderer method name (orange) for CR entries
- **Dirty flags** — `V` (vertices) and/or `M` (material), for Graphic entries only
- **Frame** — the frame number when the invalidation was registered
- **Object** — the name of the invalidated GameObject
- **Canvas** — the Canvas the object belongs to
- **Count** — how many times the same invalidation was registered in the same frame (repeated invalidations are folded into one row with a count badge)

Rows with a yellow background were captured during Play Mode; blue background rows were captured in Edit Mode.

### Details panel

Selecting an entry opens the details panel on the right:

- **Object** — full hierarchy path, with Ping and Select buttons to locate it in the scene
- **Invalidation Details** — type, frame, time, mode, and dirty flags
- **Canvas** — Canvas name and render mode
- **Components** — all components on the GameObject at capture time
- **Call-site Stack Trace** — the full managed call stack from the moment the element was queued for rebuild. Stack frames that resolve to a source file are rendered as clickable links — clicking opens the file at the correct line in your script editor. When multiple unique call stacks produced the same invalidation (folded rows), use the **◀ ▶** arrows to page through each distinct trace. Use **Copy to Clipboard** to paste the current trace into an editor or bug report.

### Common findings

- A stack trace showing `Graphic.SetVerticesDirty` or `Graphic.SetMaterialDirty` called from an `Update` or animation callback every frame means a graphic is being dirtied continuously — the most common cause of constant rebuilds.
- Layout invalidations triggered by `LayoutRebuilder.MarkLayoutForRebuild` on stable objects usually point to a script calling `SetActive`, changing a `RectTransform`, or modifying layout component properties unnecessarily.
- If many objects share the same stack trace, fix it once at the call site to eliminate all of them.
- **CR entries with a high Count** indicate a CanvasRenderer property (color, mesh, etc.) being set every frame from script. These bypass the managed rebuild path and won't appear as Layout or Graphic entries, making them easy to miss without this tool.
- **Multiple unique traces on a single folded row** (shown via the ◀ ▶ pager) means the same object is being invalidated by more than one code path in the same frame — each trace is a separate fix target.

### Requirements

- Unity 6000.0 or later
- `com.unity.ugui` 2.0.0 or later
- Native method patching requires Windows x64, macOS x64/arm64, or Linux x64 Editor. On other platforms entries are captured without stack traces.

---

## Static Rebuild Analyzer

<img width="1136" height="441" alt="image" src="https://github.com/user-attachments/assets/35e98aac-bd3a-42ba-9051-0f4b01f5e9bf" />

Detects static colliders — GameObjects with a `Collider` but no `Rigidbody` in their parent chain — that are causing physics broadphase rebuilds. Any of the following events on a static collider forces Unity to rebuild the broadphase every frame and tank physics performance:

- The GameObject moved, rotated, or scaled
- The GameObject was activated or deactivated
- A `Collider` component was added or removed
- A `Collider` component was enabled or disabled
- The GameObject was created or destroyed

**Open:** `Window > Analysis > Static Rebuild Analyzer`

### How it works

The tool snapshots the world transform and collider state of every static collider GO in the scene, waits a configurable interval, then diffs the two snapshots. Only GOs that have a `Collider` somewhere in their subtree and no `Rigidbody` anywhere in their parent chain are considered.

### Controls

| Control | Description |
|---------|-------------|
| **Interval** | Seconds between the two snapshots (0.05 – 5 s) |
| **Continuous** | When enabled, keeps re-snapshotting and updating results live; **Start/Stop** replaces **Capture** |
| **Capture / Start** | Takes the first snapshot and begins waiting |
| **Select** | Pings and selects the offending GameObject in the Hierarchy |

### Reading the results

Each row names the offending GameObject and shows the reason it was flagged:

| Label | Meaning |
|-------|---------|
| Position / Rotation / Scale | The transform changed between snapshots |
| GO Activated / GO Deactivated | `activeInHierarchy` flipped |
| GO Created / GO Destroyed | The GameObject was spawned or destroyed |
| Collider Added / Collider Removed | A `Collider` component was added or destroyed |
| Collider Enabled / Collider Disabled | A `Collider` component's `enabled` flag changed |

### Requirements

- Unity 6000.0 or later
- Must be used in **Play Mode** — transforms must be live

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
