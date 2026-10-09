# Native CPU Profiler (xctrace)

<!-- TODO: screenshot -->

Records native CPU samples with Apple's `xctrace` (the command-line side of Instruments' Time Profiler) and shows them as a mixed native + managed call tree inside the Editor. Unlike Unity's Profiler, it doesn't depend on profiler markers: every sampled stack is shown down to the engine and system functions where the time is actually spent, with the Mono JIT frames of the Editor resolved back to their C# method names.

**Open:** `Window > Analysis > Native CPU Profiler (xctrace)`

### How it works

1. **Record** — the tool runs `xctrace record --template 'Time Profiler' --attach <pid>` against the chosen process. The work runs in a detached shell script that leaves marker files in the capture folder, so a recording survives domain reloads (e.g. entering Play Mode while recording).
2. **Export** — once recording stops, `xctrace export` dumps the trace's table of contents and the `time-profile` table to XML.
3. **Parse** — the export is streamed on a background thread into samples (thread, timestamp, weight, backtrace).
4. **Symbolicate** — native frames come already symbolicated by `xctrace`. Frames with no owning binary and no symbol are treated as Mono JIT code and resolved to managed methods:
   - **Capture of this Editor** — addresses are looked up live in the Editor's own Mono runtime (`mono_jit_info_table_find`), giving fully qualified C# method names and their assemblies. The result is saved as `symbols.json` next to the capture, so the capture can be reopened later (even after the domain is gone) with its managed names intact.
   - **Capture of another process** — Mono JIT frames can't be resolved and show up as raw addresses. IL2CPP players are fully symbolicated by `xctrace` itself, since their code is native.

If a domain reload happens mid-capture, JIT code from before the reload has been freed, so those frames are grouped under a single **[JIT code unloaded by domain reload]** node rather than being mis-resolved.

Captures are stored under `Library/NativeProfiler/<timestamp>/` in the project.

### Workflow

1. Open the tool window.
2. Choose the target in the toolbar:
   - **This Editor** — profiles the running Editor (including Play Mode).
   - **Other Process** — type a pid or process name, or use **Pick** to choose from running apps (e.g. a standalone player build).
3. Set **Duration** in seconds, or `0` to record until **Stop** is pressed.
4. Click **● Record**. The status bar shows elapsed time, then the export and parsing phases.
5. Once loaded, the call tree opens on the main thread with the heaviest path expanded.

Previous captures can be reopened from the **Captures** dropdown, and any existing `.trace` made with `xctrace` or Instruments can be loaded with **Import .trace…** (managed frames are only resolvable if a `symbols.json` cache sits next to it).

> **Tip:** keep the Game view focused / Play Mode running while recording — an unfocused Editor throttles its update loop, and the capture won't reflect normal frame cost.

### Reading the results

<!-- TODO: screenshot -->

**Timeline** — a bar strip above the tree showing CPU time over the capture for the selected thread(s). Drag across it to restrict the call tree to that time range (e.g. a single spike); double-click to clear the selection.

**View toolbar:**

| Control | Meaning |
|--------|---------|
| **Top-Down / Bottom-Up** | Top-down starts from the thread entry point down to the leaves. Bottom-up inverts the tree so the roots are the functions where time is actually spent, with their callers underneath |
| **Thread** | Show a single thread, or **All threads** (one root per thread). Threads are listed by total sampled time |
| **Managed only** | Hides native frames, keeping only managed frames plus the first native function called from managed code (usually the engine binding / icall where the time goes). Stacks with no managed frames are grouped under **[native only — no managed frames]** |
| **Expand Heaviest** | Expands the path down the heaviest children while they still account for a large share of their parent's time |
| **Collapse** | Collapses the whole tree |
| **Search** | Flat list of every node whose function name matches, sorted by total time. Clearing the search reveals the selected node in the tree |

**Call tree columns:**

| Column | Meaning |
|--------|---------|
| **Total (ms)** | Time spent in the function and everything it called, along this path |
| **%** | Total time as a percentage of the current view (thread + time range) |
| **Self (ms)** | Time spent in the function itself, excluding its callees |
| **Function** | Function name. Managed methods are shown in blue, synthetic nodes (thread roots, grouping buckets) in orange, unresolved addresses in grey |
| **Module** | Binary or assembly that owns the function |

Double-click a managed function to open its script at the method in your code editor. **Copy Stack** in the footer copies the full call stack of the selected row to the clipboard.

The footer also shows the target process, sample count and total time of the current view, how many managed frames were resolved, and a note explaining how (or why not) managed frames were symbolicated.

### Exporting

- **Open in Instruments** — opens the raw `.trace` in Instruments for anything the tool doesn't cover.
- **Export Folded…** — writes the current view (thread + time range, top-down) as Brendan Gregg's folded-stack format, with managed names included. Load it in [speedscope](https://www.speedscope.app) or `flamegraph.pl` to get a flame graph.

### Requirements

- Unity 6000.2 or later
- **macOS Editor only** — relies on `xctrace`, which ships with Xcode (or the Xcode Command Line Tools). On other platforms the window shows an informational message instead
- Managed frame resolution for Mono only works for captures of the Editor itself, while the capturing session's scripting domain is still alive (or from the `symbols.json` cache it writes)
