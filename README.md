# uGUI Diagnostics Tools

A pair of Unity Editor tools for diagnosing uGUI Canvas rendering performance. Both tools are embedded packages that work in Unity 6000.0 and later.

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

## Canvas Invalidation Tracker

<img width="1373" height="775" alt="image" src="https://github.com/user-attachments/assets/87046ca0-059e-4f2d-86e2-6f784a339c29" />

Logs every call that adds an element to the Canvas layout or graphic rebuild queues, capturing the full call stack at the moment of invalidation. Use this tool to find out what code is causing unnecessary rebuilds every frame.

**Open:** `Window > Analysis > Canvas Invalidation Tracker`

### How it works

The tool patches four methods in `CanvasUpdateRegistry` at the native code level using a 14-byte JMP detour (Windows, macOS, and Linux Editor builds are supported). When any of these methods is called, a hook captures the current stack trace and stores it. Just before the Canvas processes its rebuild queues, the tool pairs each queued element with its captured trace and records an entry.

The patched methods are:
- `RegisterCanvasElementForLayoutRebuild`
- `TryRegisterCanvasElementForLayoutRebuild`
- `RegisterCanvasElementForGraphicRebuild`
- `TryRegisterCanvasElementForGraphicRebuild`

The **Traces ON / Traces OFF** indicator in the toolbar shows whether patching succeeded. If it shows OFF, entries are still captured but without call stacks.

### Toolbar controls

| Control | Description |
|---------|-------------|
| **Clear** | Removes all captured entries |
| **Pause / Resume** | Temporarily stops capturing new entries |
| **Layout / Graphic** | Toggle filters for each invalidation type |
| **Max** | Maximum number of entries to keep (oldest are trimmed) |
| **Traces ON/OFF** | Green = patching active, Orange = patching inactive |

### Reading the entry list

Each row in the list shows:
- **Type badge** — `LAYOUT` (blue) or `GRAPHIC` (green)
- **Dirty flags** — `V` (vertices) and/or `M` (material), for Graphic entries only
- **Frame** — the frame number when the invalidation was registered
- **Object** — the name of the invalidated GameObject
- **Canvas** — the Canvas the object belongs to

Rows with a yellow background were captured during Play Mode; blue background rows were captured in Edit Mode.

### Details panel

Selecting an entry opens the details panel on the right:

- **Object** — full hierarchy path, with Ping and Select buttons to locate it in the scene
- **Invalidation Details** — type, frame, time, mode, and dirty flags
- **Canvas** — Canvas name and render mode
- **Components** — all components on the GameObject at capture time
- **Call-site Stack Trace** — the full managed call stack from the moment the element was queued for rebuild. Use **Copy to Clipboard** to paste it into an editor or bug report.

### Common findings

- A stack trace showing `Graphic.SetVerticesDirty` or `Graphic.SetMaterialDirty` called from an `Update` or animation callback every frame means a graphic is being dirtied continuously — the most common cause of constant rebuilds.
- Layout invalidations triggered by `LayoutRebuilder.MarkLayoutForRebuild` on stable objects usually point to a script calling `SetActive`, changing a `RectTransform`, or modifying layout component properties unnecessarily.
- If many objects share the same stack trace, fix it once at the call site to eliminate all of them.

### Requirements

- Unity 6000.0 or later
- `com.unity.ugui` 2.0.0 or later
- Native method patching requires Windows x64, macOS x64/arm64, or Linux x64 Editor. On other platforms entries are captured without stack traces.
