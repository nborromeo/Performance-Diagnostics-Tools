# Build Log Analyzer

<img width="1410" height="725" alt="image" src="https://github.com/user-attachments/assets/3b0a3399-f75b-4c0a-b812-226b92014a3f" />

Parses a Unity Editor log file and surfaces timing and diagnostic data across multiple categories in a set of sortable tabs. Also generates a Chrome-compatible trace file so you can load the entire build timeline into `chrome://tracing` or Perfetto.

**Open:** `Window > Analysis > Build Log Analyzer`

### Workflow

1. Click **Browse…** to pick a `.log` or `.txt` Editor log file, or paste its path directly into the text field.
2. Click **Parse** to scan the file and populate all tabs at once.
3. Switch between tabs to explore each category. Use the **Filter** field (where available) to narrow results by name (case-insensitive substring match). Click **✕** to clear.
4. Click any column header to sort. Click a row to ping and select the corresponding asset in the Project window (where applicable).

Optionally, click **Generate Trace** to produce a `buildLogTrace.json` file at the root of your project. Open this file in `chrome://tracing` or [ui.perfetto.dev](https://ui.perfetto.dev) to view the full build timeline as a flame graph.

The parser ignores any log-line prefix (timestamps, thread IDs, `[Step N/M]` CI brackets, etc.), so logs produced by the Editor, CI pipelines, or third-party log decorators are all handled correctly.

### Tab — Shader Compilation

Shows the stripping and compilation data for every shader pass block found in the log. Use it to find which shaders took the longest to compile, how many variants survived each stripping stage, and how much work was served from cache versus compiled from source.

| Column | Meaning |
|--------|---------|
| **#** | Parse order — reflects the order in which the blocks appeared in the log |
| **Shader** | Shader name as reported by Unity (e.g. `Universal Render Pipeline/Unlit`) |
| **Pass** | Pass name from the `Pass "…"` declaration; empty for unnamed passes |
| **Stage** | Shader stage: Vertex, Fragment, Geometry, Hull, or Domain |
| **API** | Graphics API this compilation targeted (e.g. `Metal`, `Vulkan`, `d3d11`) |
| **Full Space** | Total theoretical variant count before any stripping |
| **After Settings** | Variants remaining after Unity's graphics settings filter |
| **After Built-in** | Variants remaining after Unity's built-in stripping pass |
| **After Scriptable** | Variants remaining after all `IShaderVariantStripper` scriptable strippers |
| **Strip Time (s)** | Wall-clock seconds reported for the stripping phase |
| **Compile Time (s)** | Wall-clock seconds for the full compilation of this pass |
| **Local Cache** | Local cache hits and the CPU time spent on those lookups |
| **Remote Cache** | Remote cache hits and the CPU time spent on those lookups |
| **Compiled (CPU)** | Variants compiled from source and the cumulative CPU time across all compiler threads (can exceed wall time when parallel compilation is active) |

The table defaults to **Full Space descending** so the heaviest shaders appear first.

- A large gap between **Full Space** and **After Scriptable** means your strippers are doing useful work. If the gap is small, consider writing or enabling a `IShaderVariantStripper`.
- High **Compiled (CPU)** time with low cache hits means variants are being compiled from scratch on every build. Warming the local or remote cache (incremental builds, build cache server) will speed up subsequent builds.
- The **Strip Time** and **Compile Time** columns together show whether the bottleneck is in the stripping phase or the actual GPU compiler invocations.

### Tab — Asset Import

Lists every asset import event found in the log. Defaults to **Time (s) descending** so the most expensive imports appear first. Clicking a row pings and selects the asset in the Project window.

| Column | Meaning |
|--------|---------|
| **Line** | Log file line number of the first import of this asset |
| **Asset** | Asset name (hover to see the full path) |
| **Time (s)** | Total import time in seconds, summed across all imports of this asset |
| **Count** | Number of times this asset was imported |

### Tab — Asset DB Refreshes

Lists every Asset Database refresh block found in the log, each identified by its Asset Pipeline refresh GUID.

| Column | Meaning |
|--------|---------|
| **Line** | Log file line number |
| **Refresh ID** | Asset Pipeline refresh GUID |
| **Time (s)** | Total refresh duration |
| **Reason** | What initiated the refresh |

Selecting a refresh row and switching to the **Asset Import** tab navigates directly to the imports that occurred within that refresh, and vice versa — the two tabs cross-link to each other.

### Tab — Script Recompilations

Lists every script compilation cycle found in the log, showing what triggered it and how long the Tundra build took.

| Column | Meaning |
|--------|---------|
| **Line** | Log file line where the script compilation block started |
| **Reasons** | What triggered this recompilation (pipe-separated list if multiple reasons) |
| **Total Time (s)** | Cumulative time of all Tundra build success entries associated with this recompilation |

### Tab — Addressables Builds

Lists every Addressables content build found in the log.

| Column | Meaning |
|--------|---------|
| **Line** | Log file line where the Addressables build started |
| **Duration** | Total build duration as reported in the log |

### Tab — Timeline (Summary)

Aggregates the entries reported by every other tab into a single chronological table, so the order in which processes ran during the build can be seen at a glance without switching tabs.

| Column | Meaning |
|--------|---------|
| **Line** | Log file line range of the entry (start–end) |
| **Tab** | Which tab this entry came from (Shader Compilation, Asset Import, etc.) |
| **Name** | Entry name — click to jump straight to the entry in its origin tab, with the row selected there |
| **Time (s)** | Time taken by this process |
| **Timestamp** | Log timestamp at the start line, only shown when the log has timestamps |

Use the **Filter** field to narrow by name or source tab. This tab is populated last, after every other tab has finished parsing, so it always reflects the full set of results.

### Requirements

- Any Unity Editor log file; no minimum Unity version restriction
- Chrome trace generation requires a timestamped log (the Editor's default log format includes timestamps)
