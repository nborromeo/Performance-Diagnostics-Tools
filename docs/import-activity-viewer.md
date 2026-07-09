# Import Activity Viewer

<img width="1189" height="330" alt="image" src="https://github.com/user-attachments/assets/dd7ee7d1-ce91-4359-a67d-70da119a84bc" />

A companion to Unity's built-in Import Activity window. Instead of a flat list of every asset that was reimported, this groups them by cascade: each row on the left is a root asset that was imported for its own reason (edited, VCS update, etc.), and selecting it shows the full chain of dependents it dragged along on the right.

**Open:** `Window > Analysis > Import Activity Viewer`

### How it works

The tool reads Unity's internal Import Activity data via reflection and diffs each asset's current revision against its previous one to determine what caused the reimport:

- **Primary signal** — Unity's own `ArtifactDifferenceReporter` output (the same "reason for reimport" text the built-in Import Activity window shows) gives a deterministic cause → effect edge per asset.
- **Fallback signal** — for assets with no previous cached revision to diff against, the tool matches the raw dependency GUID set against other assets reimported in the same batch. This is weaker (it doesn't prove the dependency's hash actually changed) and is only used when the primary signal is unavailable.

Assets with no identified cause become root nodes; everything transitively pulled in under a root forms its cascade tree.

### Workflow

1. Open the tool window. It analyzes the current Import Activity data automatically.
2. The left pane lists every root asset, with the size and cost of its reimport cascade.
3. Click a root to see its full dependency chain in the right-hand tree.
4. Use the search fields above each pane to filter by asset name or path.
5. Click **Refresh** to re-analyze after new imports have occurred.

### Reading the results

| Column (root list) | Meaning |
|--------|---------|
| **Root Asset** | The asset that triggered the cascade |
| **Assets Affected** | How many dependent assets were reimported as a result |
| **Total Import Time** | Summed import time across the root and every dependent it dragged along |
| **Last Import** | Timestamp of the root's most recent import |
| **Reason** | Why the root was reimported, or that it had no dependents |

Selecting a root expands a tree on the right showing every dependent asset, its own import time and importer, and the reason it was pulled in (either the diff-reported reason or "Dependency of `<parent>`" when inferred from the dependency graph).

Cascades are sorted by **Assets Affected** then **Total Import Time**, so the most expensive reimport chains surface first — a large cascade from a single edited asset is usually the best target for reducing reimport cost.

### Requirements

- Unity 6000.0 or later
- Relies on Unity's internal Import Activity API via reflection; behavior may vary across Editor versions
