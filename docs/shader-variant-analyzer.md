# Shader Variant Analyzer

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
