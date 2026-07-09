# UI Batch Highlighter

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
