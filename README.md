# Unity Performance Diagnostics Tools

A collection of Unity Editor tools for diagnosing rendering and physics performance. All tools are embedded packages that work in Unity 6000.0 and later.

---

## Runtime Project Auditor (aka Performance Diagnostics)

<img width="1336" height="456" alt="image" src="https://github.com/user-attachments/assets/7a57c3ef-4553-4b46-b09b-163ebb6733d0" />

A unified window that runs multiple diagnostic detectors — Canvas Invalidation and Static Rebuild — simultaneously and collects all findings into a single, sortable list.

**Open:** `Window > Analysis > Performance Diagnostics`

📄 [Full documentation](docs/performance-diagnostics.md)

---

## Shader Variant Analyzer

<img width="1080" height="492" alt="image" src="https://github.com/user-attachments/assets/9a360136-a1af-402e-9fb4-35f1055f8735" />

Analyzes a shader's keyword declarations and the materials in the project that reference it, giving you a clear picture of how many shader variants are being compiled and what is driving that count.

**Open:** `Window > Analysis > Shader Variant Analyzer`

📄 [Full documentation](docs/shader-variant-analyzer.md)

---

## Build Log Analyzer

<img width="1410" height="725" alt="image" src="https://github.com/user-attachments/assets/3b0a3399-f75b-4c0a-b812-226b92014a3f" />

Parses a Unity Editor log file and surfaces timing and diagnostic data across multiple categories in a set of sortable tabs. Also generates a Chrome-compatible trace file for `chrome://tracing` or Perfetto.

**Open:** `Window > Analysis > Build Log Analyzer`

📄 [Full documentation](docs/build-log-analyzer.md)

---

## Import Activity Viewer

<img width="1189" height="330" alt="image" src="https://github.com/user-attachments/assets/dd7ee7d1-ce91-4359-a67d-70da119a84bc" />

A companion to Unity's built-in Import Activity window that groups reimported assets by cascade, so you can see the full chain of dependents each root reimport dragged along.

**Open:** `Window > Analysis > Import Activity Viewer`

📄 [Full documentation](docs/import-activity-viewer.md)

---

## Scene Build Optimizer

Generates optimized copies of scenes ahead of a build — without touching the authored scenes or assets — via a pluggable, explicitly-ordered set of optimizers. Ships with a Terrain Layer Optimizer that strips unused `TerrainLayer`s (detected with a Burst job over raw alphamap texture memory) and repacks the terrain's alphamaps, and a Terrain Tile Merger that combines NxN grids of adjacent Terrain tiles into fewer, larger ones with no seam in the merged heightmap or splatmap.

**Open:** `Window > Analysis > Scene Build Optimizer`

📄 [Full documentation](docs/scene-build-optimizer.md)

---

## UI Batch Highlighter

<img width="921" height="793" alt="image" src="https://github.com/user-attachments/assets/1111084c-1550-41b6-83d1-bfa48036ef22" />

Visualizes every draw batch produced by a Canvas directly in the Scene and Game views, highlighting the first and last element of each batch so you can quickly identify what is causing batch breaks.

**Open:** `Window > Analysis > UI Batch Highlighter`

📄 [Full documentation](docs/ui-batch-highlighter.md)
