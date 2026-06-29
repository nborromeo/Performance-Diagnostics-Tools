using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShaderVariantAnalyzer.Editor
{
    public class ShaderVariantAnalyzerWindow : EditorWindow
    {
        // ── Data models ────────────────────────────────────────────────────────

        sealed class KeywordData
        {
            public string Name;
            public int    PermutationCount;
            public int    MaterialCount;
            public string FilePath;  // absolute path to the file where the pragma was found
            public int    Line;      // 1-based line number
        }

        sealed class MultiCompileData
        {
            public string   Name;        // display label, e.g. "FOG_ON | FOG_EXP2"
            public string[] Options;     // relevant options for this shader, including "_"
            public int      OptionCount => Options.Length;
            public bool     IsBuiltin;   // true = not found in parsed source (Unity built-in)
            public string   FilePath;    // null for built-in
            public int      Line;        // 1-based line number, 0 if unknown
        }

        sealed class PermutationData
        {
            public string         Hash;
            public string[]       Keywords;
            public List<Material> Materials = new List<Material>();

            public string KeywordsLabel => Keywords.Length == 0
                ? "(base — no shader_feature keywords)"
                : string.Join(", ", Keywords);
        }

        sealed class ParsedShaderInfo
        {
            // SF keyword name → (absolute file path, 1-based line); first declaration wins.
            public Dictionary<string, (string file, int line)>              SfLocations = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
            // All MC pragma sets with their source location.
            public List<(string[] tokens, string file, int line)>           McSets      = new List<(string[], string, int)>();

            public HashSet<string> SfKeywords => new HashSet<string>(SfLocations.Keys, StringComparer.Ordinal);
        }

        // ── Data models ────────────────────────────────────────────────────────

        sealed class LogShaderEntry
        {
            public string ShaderName;
            public string PassName;
            public int    SubShaderIndex;
            public string ShaderType;
            public string Pipeline;
            public int    CompiledVariants;
            public int    TotalVariants;
            public string GraphicsAPI;
            public long   FullVariantSpace;
            public long   AfterSettingsFilter;
            public long   AfterBuiltinStripping;
            public long   AfterScriptableStripping;
            public float  ProcessTimeSec;
        }

        sealed class LogTreeView : TreeView<int>
        {
            List<LogShaderEntry> m_Source = new List<LogShaderEntry>();

            public LogTreeView(TreeViewState<int> tvState, MultiColumnHeader header) : base(tvState, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<LogShaderEntry> entries)
            {
                m_Source = entries;
                SortSource();
                Reload();
            }

            void SortSource()
            {
                int col = multiColumnHeader.sortedColumnIndex;
                if (col < 0 || m_Source.Count == 0) return;
                bool asc = multiColumnHeader.IsSortedAscending(col);
                m_Source.Sort((a, b) =>
                {
                    int cmp = col switch
                    {
                        0 => string.Compare(a.ShaderName, b.ShaderName, StringComparison.OrdinalIgnoreCase),
                        1 => string.Compare(a.PassName,   b.PassName,   StringComparison.OrdinalIgnoreCase),
                        2 => string.Compare(a.ShaderType, b.ShaderType, StringComparison.OrdinalIgnoreCase),
                        3 => a.FullVariantSpace.CompareTo(b.FullVariantSpace),
                        4 => a.AfterSettingsFilter.CompareTo(b.AfterSettingsFilter),
                        5 => a.AfterBuiltinStripping.CompareTo(b.AfterBuiltinStripping),
                        6 => a.AfterScriptableStripping.CompareTo(b.AfterScriptableStripping),
                        7 => a.ProcessTimeSec.CompareTo(b.ProcessTimeSec),
                        _ => 0
                    };
                    return asc ? cmp : -cmp;
                });
            }

            protected override TreeViewItem<int> BuildRoot()
            {
                var root  = new TreeViewItem<int>(-1, -1);
                var items = new List<TreeViewItem<int>>(m_Source.Count);
                for (int i = 0; i < m_Source.Count; i++)
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].ShaderName));
                SetupParentsAndChildrenFromDepths(root, items);
                return root;
            }

            protected override void SingleClickedItem(int id)
            {
                if (id < 0 || id >= m_Source.Count) return;
                string shaderName = m_Source[id].ShaderName;

                // 1. Runtime lookup — fast, works for .shader files already compiled.
                var shader = Shader.Find(shaderName);
                if (shader != null) { PingAndSelect(shader); return; }

                // The last segment of "Shader Graphs/MyGraph" gives the asset filename.
                string hint = shaderName.Contains('/')
                    ? shaderName.Substring(shaderName.LastIndexOf('/') + 1)
                    : shaderName;

                // 2. Load every asset matching the filename hint and verify by shader name.
                //    Using no type filter casts the widest net (covers .shader, .shadergraph,
                //    and any other packaging).
                foreach (string guid in AssetDatabase.FindAssets(hint))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);

                    // Try loading as a compiled Shader asset first.
                    var asShader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (asShader != null && asShader.name == shaderName)
                    {
                        PingAndSelect(asShader);
                        return;
                    }

                    // For .shadergraph files the main asset name is just the file's basename,
                    // not the full "Shader Graphs/..." path, so skip the name check and trust
                    // that the filename hint is specific enough.
                    if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                    {
                        var main = AssetDatabase.LoadMainAssetAtPath(path);
                        if (main != null) { PingAndSelect(main); return; }
                    }
                }
            }

            static void PingAndSelect(UnityEngine.Object obj)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var e = m_Source[args.item.id];
                for (int i = 0; i < args.GetNumVisibleColumns(); i++)
                {
                    var rect = args.GetCellRect(i);
                    CenterRectUsingSingleLineHeight(ref rect);
                    string text = args.GetColumn(i) switch
                    {
                        0 => e.ShaderName,
                        1 => e.PassName,
                        2 => e.ShaderType,
                        3 => e.FullVariantSpace.ToString("N", s_DotGroupFmt),
                        4 => e.AfterSettingsFilter.ToString("N", s_DotGroupFmt),
                        5 => e.AfterBuiltinStripping.ToString("N", s_DotGroupFmt),
                        6 => e.AfterScriptableStripping.ToString("N", s_DotGroupFmt),
                        7 => e.ProcessTimeSec > 0f ? e.ProcessTimeSec.ToString("F3") : "—",
                        _ => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Shader"),          width = 200, minWidth = 80,  autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Pass"),             width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Stage"),            width = 70,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Full Space"),       width = 80,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Settings"),   width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Built-in"),   width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Scriptable"), width = 100, minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Proc. Time (s)"),   width = 90,  minWidth = 60,  autoResize = false, canSort = true },
                });
                // Default: Full Space descending
                state.sortedColumnIndex          = 3;
                state.columns[3].sortedAscending = false;
                return state;
            }
        }

        // ── Window state ──────────────────────────────────────────────────────

        Shader m_Shader;
        bool   m_IsAnalyzed;
        int    m_ActiveTab;
        string m_StatusMessage = "Select a shader and click Analyze.";

        // Tab 0 — Shader Feature Keywords
        readonly List<KeywordData> m_SfKeywords = new List<KeywordData>();
        int  m_KwSortCol = 1;
        bool m_KwSortAsc = false;
        int  m_SelectedKwIdx = -1;
        Vector2 m_KwScroll, m_KwDetailScroll;

        // Tab 1 — Multi-Compile Keywords
        readonly List<MultiCompileData> m_McKeywords = new List<MultiCompileData>();
        int  m_McSortCol = 1;
        bool m_McSortAsc = false;
        int  m_SelectedMcIdx = -1;
        Vector2 m_McScroll;

        // Tab 2 — Permutations
        readonly List<PermutationData> m_Permutations = new List<PermutationData>();
        int  m_PermSortCol = 1;
        bool m_PermSortAsc = true;
        int  m_SelectedPermIdx = -1;
        Vector2 m_PermScroll, m_PermDetailScroll;

        // Tab 3 — Editor Log
        readonly List<LogShaderEntry> m_LogEntries        = new List<LogShaderEntry>();
        readonly List<LogShaderEntry> m_LogFilteredEntries = new List<LogShaderEntry>();
        string       m_LogFilePath  = string.Empty;
        string       m_LogStatusMsg = string.Empty;
        string       m_LogFilter    = string.Empty;
        [SerializeField] TreeViewState<int> m_LogTreeState;
        LogTreeView  m_LogTreeView;

        static readonly string[] k_TabNames =
            { "Shader Feature Keywords", "Multi-Compile Keywords", "Permutations", "Editor Log" };

        const float k_RowH       = 22f;
        const float k_DetailMinH = 120f;
        static readonly Color k_SelectColor = new Color(0.24f, 0.49f, 0.91f, 0.35f);
        static readonly Color k_AltRowColor = new Color(0f, 0f, 0f, 0.04f);

        // ── Shader source parsing ──────────────────────────────────────────────

        static readonly Regex s_PragmaRx = new Regex(
            @"^\s*#pragma\s+(\w+)(.*)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        static readonly Regex s_IncludeRx = new Regex(
            @"^\s*#include(?:_with_pragmas)?\s+""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.Multiline);

        static ParsedShaderInfo ParseShaderSource(Shader shader)
        {
            var info = new ParsedShaderInfo();
            string assetPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(assetPath)) return info;

            string fullPath = Path.GetFullPath(assetPath);
            ParseFile(fullPath, info, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return info;
        }

        static void ParseFile(string fullPath, ParsedShaderInfo info, HashSet<string> visited)
        {
            if (!visited.Add(fullPath)) return;
            if (!File.Exists(fullPath)) return;

            string[] lines;
            try { lines = File.ReadAllLines(fullPath); }
            catch { return; }

            string dir = Path.GetDirectoryName(fullPath);

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed   = lines[i].Trim();
                int    lineNumber = i + 1;

                if (trimmed.StartsWith("#pragma ") || trimmed.StartsWith("#pragma\t"))
                {
                    var m = s_PragmaRx.Match(trimmed);
                    if (!m.Success) continue;

                    string kind = m.Groups[1].Value;
                    bool isSf   = kind.StartsWith("shader_feature", StringComparison.OrdinalIgnoreCase);
                    bool isMc   = kind.StartsWith("multi_compile",  StringComparison.OrdinalIgnoreCase);
                    if (!isSf && !isMc) continue;

                    string rest = m.Groups[2].Value;
                    int ci = rest.IndexOf("//", StringComparison.Ordinal);
                    if (ci >= 0) rest = rest.Substring(0, ci);

                    var tokens = rest.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length == 0) continue;

                    if (isSf)
                    {
                        foreach (var tok in tokens)
                            if (tok != "_" && !info.SfLocations.ContainsKey(tok))
                                info.SfLocations[tok] = (fullPath, lineNumber);
                    }
                    else
                    {
                        info.McSets.Add((tokens, fullPath, lineNumber));
                    }
                }
                else if (trimmed.StartsWith("#include"))
                {
                    var m = s_IncludeRx.Match(trimmed);
                    if (!m.Success) continue;
                    string resolved = ResolveInclude(dir, m.Groups[1].Value);
                    ParseFile(resolved, info, visited);
                }
            }
        }

        static string ResolveInclude(string currentDir, string includePath)
        {
            // 1. Relative to the including file (most common case).
            string candidate = Path.GetFullPath(Path.Combine(currentDir, includePath));
            if (File.Exists(candidate)) return candidate;

            // 2. Relative to project root — covers "Assets/..." and embedded/local "Packages/...".
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            candidate = Path.GetFullPath(Path.Combine(projectRoot, includePath));
            if (File.Exists(candidate)) return candidate;

            // 3. "Packages/com.foo.bar/some/file.hlsl" → Library/PackageCache/com.foo.bar@version/some/file.hlsl
            if (includePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                string withoutPrefix = includePath.Substring("Packages/".Length);
                int slash = withoutPrefix.IndexOf('/');
                if (slash > 0)
                {
                    string packageId = withoutPrefix.Substring(0, slash);
                    string rest      = withoutPrefix.Substring(slash + 1);
                    string cacheDir  = Path.Combine(projectRoot, "Library", "PackageCache");
                    if (Directory.Exists(cacheDir))
                    {
                        foreach (string pkgDir in Directory.GetDirectories(cacheDir, packageId + "@*"))
                        {
                            candidate = Path.GetFullPath(Path.Combine(pkgDir, rest));
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
            }

            return candidate; // not found — ParseFile will skip gracefully via File.Exists guard
        }

        static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return string.Empty;
            string root = Path.GetDirectoryName(Application.dataPath);
            if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
            return absolutePath;
        }

        static void OpenFileAtLine(string absolutePath, int line)
        {
            string relative = ToProjectRelativePath(absolutePath).Replace('\\', '/');
            if (!string.IsNullOrEmpty(relative))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(relative);
                if (asset != null && AssetDatabase.OpenAsset(asset, line))
                    return;
            }
            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(absolutePath, line);
        }

        // ── Menu ──────────────────────────────────────────────────────────────

        [MenuItem("Window/Analysis/Shader Variant Analyzer")]
        static void Open() => GetWindow<ShaderVariantAnalyzerWindow>("Shader Variant Analyzer");

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void OnEnable() { minSize = new Vector2(480, 320); }

        // ── Root GUI ──────────────────────────────────────────────────────────

        void OnGUI()
        {
            DrawToolbar();

            if (!string.IsNullOrEmpty(m_StatusMessage))
                EditorGUILayout.HelpBox(m_StatusMessage, MessageType.Info);

            m_ActiveTab = GUILayout.Toolbar(m_ActiveTab, k_TabNames);
            GUILayout.Space(4f);

            switch (m_ActiveTab)
            {
                case 0: if (m_IsAnalyzed) DrawShaderFeatureTab(); break;
                case 1: if (m_IsAnalyzed) DrawMultiCompileTab();  break;
                case 2: if (m_IsAnalyzed) DrawPermutationsTab();  break;
                case 3: DrawEditorLogTab(); break;
            }
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField("Shader:", GUILayout.Width(50f));
            var newShader = (Shader)EditorGUILayout.ObjectField(
                m_Shader, typeof(Shader), false, GUILayout.Width(240f));

            if (newShader != m_Shader)
            {
                m_Shader        = newShader;
                m_IsAnalyzed    = false;
                m_StatusMessage = m_Shader == null
                    ? "Select a shader and click Analyze."
                    : $"\"{m_Shader.name}\" selected — click Analyze.";
            }

            using (new EditorGUI.DisabledScope(m_Shader == null))
            {
                if (GUILayout.Button("Analyze", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                    Analyze();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Tab 0: Shader Feature Keywords ────────────────────────────────────

        void DrawShaderFeatureTab()
        {
            float w    = ContentWidth();
            float[] colW = { w * 0.55f, w * 0.225f, w * 0.225f };

            DrawColumnHeaders(
                new[] { "Keyword", "Permutations", "Materials" }, colW,
                ref m_KwSortCol, ref m_KwSortAsc, SortSfKeywords);

            bool hasDetail = m_SelectedKwIdx >= 0 && m_SelectedKwIdx < m_SfKeywords.Count;
            float listH    = hasDetail
                ? Mathf.Max(k_RowH * 4f, position.height * 0.45f - 100f)
                : position.height - 100f;

            m_KwScroll = EditorGUILayout.BeginScrollView(m_KwScroll, GUILayout.Height(listH));
            for (int i = 0; i < m_SfKeywords.Count; i++)
            {
                var kd = m_SfKeywords[i];
                DrawSelectableRow(i, m_SelectedKwIdx, i % 2 == 1,
                    () => m_SelectedKwIdx = m_SelectedKwIdx == i ? -1 : i,
                    () =>
                    {
                        GUILayout.Label(kd.Name,                        EditorStyles.label, GUILayout.Width(colW[0]));
                        GUILayout.Label(kd.PermutationCount.ToString(), GUILayout.Width(colW[1]));
                        GUILayout.Label(kd.MaterialCount.ToString(),    GUILayout.Width(colW[2]));
                    });
            }
            if (m_SfKeywords.Count == 0)
                GUILayout.Label("No shader_feature keywords found.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();

            if (hasDetail)
                DrawKwDetail(m_SfKeywords[m_SelectedKwIdx]);
        }

        void DrawKwDetail(KeywordData kd)
        {
            GUILayout.Space(4f);

            if (!string.IsNullOrEmpty(kd.FilePath))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Defined in:", EditorStyles.boldLabel, GUILayout.Width(72f));
                string display = ToProjectRelativePath(kd.FilePath) + ":" + kd.Line;
                if (GUILayout.Button(display, EditorStyles.linkLabel))
                    OpenFileAtLine(kd.FilePath, kd.Line);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }

            GUILayout.Label($"Permutations containing \"{kd.Name}\":", EditorStyles.boldLabel);

            m_KwDetailScroll = EditorGUILayout.BeginScrollView(
                m_KwDetailScroll, GUILayout.Height(k_DetailMinH));

            bool any = false;
            foreach (var perm in m_Permutations)
            {
                if (!Array.Exists(perm.Keywords, k => k == kd.Name)) continue;
                any = true;
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUILayout.Label(perm.KeywordsLabel, EditorStyles.wordWrappedLabel);
                GUILayout.Label($"{perm.Materials.Count} mat(s)", EditorStyles.miniLabel, GUILayout.Width(60f));
                EditorGUILayout.EndHorizontal();
            }
            if (!any)
                GUILayout.Label(
                    "Keyword not used by any material (zero permutations).",
                    EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.EndScrollView();
        }

        // ── Tab 1: Multi-Compile Keywords ─────────────────────────────────────

        void DrawMultiCompileTab()
        {
            float w    = ContentWidth();
            float[] colW = { w * 0.72f, w * 0.14f, w * 0.14f };

            DrawColumnHeaders(
                new[] { "Keyword / Set", "Options", "Type" }, colW,
                ref m_McSortCol, ref m_McSortAsc, SortMcKeywords);

            bool hasDetail = m_SelectedMcIdx >= 0 && m_SelectedMcIdx < m_McKeywords.Count;
            float listH    = hasDetail
                ? Mathf.Max(k_RowH * 4f, position.height * 0.55f - 100f)
                : position.height - 100f;

            m_McScroll = EditorGUILayout.BeginScrollView(m_McScroll, GUILayout.Height(listH));
            for (int i = 0; i < m_McKeywords.Count; i++)
            {
                var mc = m_McKeywords[i];
                DrawSelectableRow(i, m_SelectedMcIdx, i % 2 == 1,
                    () => m_SelectedMcIdx = m_SelectedMcIdx == i ? -1 : i,
                    () =>
                    {
                        GUILayout.Label(mc.Name,                   EditorStyles.label,     GUILayout.Width(colW[0]));
                        GUILayout.Label(mc.OptionCount.ToString(),                          GUILayout.Width(colW[1]));
                        GUILayout.Label(mc.IsBuiltin ? "Built-in" : "User", EditorStyles.miniLabel, GUILayout.Width(colW[2]));
                    });
            }
            if (m_McKeywords.Count == 0)
                GUILayout.Label("No multi_compile keywords found.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();

            if (hasDetail)
                DrawMcDetail(m_McKeywords[m_SelectedMcIdx]);
            else if (m_McKeywords.Count > 0)
            {
                int userSets   = m_McKeywords.Count(mc => !mc.IsBuiltin);
                int builtinKws = m_McKeywords.Count(mc => mc.IsBuiltin);
                EditorGUILayout.HelpBox(
                    $"{userSets} user-defined pragma set(s)  ·  {builtinKws} built-in keyword(s)",
                    MessageType.Info);
            }
        }

        void DrawMcDetail(MultiCompileData mc)
        {
            GUILayout.Space(4f);

            if (!mc.IsBuiltin && !string.IsNullOrEmpty(mc.FilePath))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Defined in:", EditorStyles.boldLabel, GUILayout.Width(72f));
                string display = ToProjectRelativePath(mc.FilePath) + ":" + mc.Line;
                if (GUILayout.Button(display, EditorStyles.linkLabel))
                    OpenFileAtLine(mc.FilePath, mc.Line);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("Built-in keyword injected by Unity — no user source location.", MessageType.None);
            }

            GUILayout.Space(4f);
            GUILayout.Label("Options in this set:", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(string.Join("  |  ", mc.Options), EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();
        }

        // ── Tab 2: Permutations ───────────────────────────────────────────────

        void DrawPermutationsTab()
        {
            float w    = ContentWidth();
            float[] colW = { w * 0.72f, w * 0.28f };

            DrawColumnHeaders(
                new[] { "Active Shader Feature Keywords", "Materials" }, colW,
                ref m_PermSortCol, ref m_PermSortAsc, SortPermutations);

            bool hasDetail = m_SelectedPermIdx >= 0 && m_SelectedPermIdx < m_Permutations.Count;
            float listH    = hasDetail
                ? Mathf.Max(k_RowH * 4f, position.height * 0.45f - 100f)
                : position.height - 100f;

            m_PermScroll = EditorGUILayout.BeginScrollView(m_PermScroll, GUILayout.Height(listH));
            for (int i = 0; i < m_Permutations.Count; i++)
            {
                var perm = m_Permutations[i];
                DrawSelectableRow(i, m_SelectedPermIdx, i % 2 == 1,
                    () => m_SelectedPermIdx = m_SelectedPermIdx == i ? -1 : i,
                    () =>
                    {
                        GUILayout.Label(perm.KeywordsLabel,              EditorStyles.label, GUILayout.Width(colW[0]));
                        GUILayout.Label(perm.Materials.Count.ToString(), GUILayout.Width(colW[1]));
                    });
            }
            if (m_Permutations.Count == 0)
                GUILayout.Label("No permutations found.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();

            if (hasDetail)
                DrawPermDetail(m_Permutations[m_SelectedPermIdx]);
        }

        void DrawPermDetail(PermutationData perm)
        {
            GUILayout.Space(4f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Active Keywords:", EditorStyles.boldLabel);
            GUILayout.Label(perm.KeywordsLabel, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();

            GUILayout.Label($"Materials ({perm.Materials.Count}):", EditorStyles.boldLabel);

            m_PermDetailScroll = EditorGUILayout.BeginScrollView(
                m_PermDetailScroll, GUILayout.Height(k_DetailMinH));

            for (int i = 0; i < perm.Materials.Count; i++)
            {
                var mat = perm.Materials[i];
                if (mat == null) continue;

                if (i % 2 == 1)
                {
                    var r = GUILayoutUtility.GetRect(
                        GUIContent.none, EditorStyles.label,
                        GUILayout.Height(k_RowH), GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(r, k_AltRowColor);
                    var buttonStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
                    if (GUI.Button(r, mat.name, buttonStyle))
                    {
                        Selection.activeObject = mat;
                        EditorGUIUtility.PingObject(mat);
                    }
                }
                else
                {
                    if (GUILayout.Button(mat.name, EditorStyles.label, GUILayout.Height(k_RowH)))
                    {
                        Selection.activeObject = mat;
                        EditorGUIUtility.PingObject(mat);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ── Tab 3: Editor Log ─────────────────────────────────────────────────

        // Only the two complex matches still use Regex. Hot-path line routing uses IndexOf.
        // Captures the shader name from:  Compiling shader "Universal Render Pipeline/Unlit"
        static readonly Regex s_LogCompilingShaderRx = new Regex(
            @"^Compiling shader ""(.+)""",
            RegexOptions.Compiled);

        // Number format that uses "." as the thousands separator (e.g. 1.024.576).
        static readonly System.Globalization.NumberFormatInfo s_DotGroupFmt =
            new System.Globalization.NumberFormatInfo
            {
                NumberGroupSeparator = ".",
                NumberGroupSizes     = new[] { 3 },
                NumberDecimalDigits  = 0,
            };

        // Group 1 = shader+pass concatenated, Group 2 = light-mode tag (not the pass name),
        // Group 3 = SubShader, Group 4 = ShaderType, Group 5 = Pipeline,
        // Group 6/7 = compiled/total, Group 8 = time ms
        static readonly Regex s_LogShaderLineRx = new Regex(
            @"^Shader=(.+?)\s+\((\w[\w\s]*?)\)\s+\(SubShader:\s*(\d+)\)\s+\(ShaderType:\s*(\w+)\)\s+Pipeline=(\S*)\s+Total=(\d+)/(\d+)\([^)]+\)\s+Time=([\d.]+)ms",
            RegexOptions.Compiled);

        // Strips every non-digit character so locale separators (. , space) don't break parsing.
        static readonly Regex s_NonDigitRx = new Regex(@"[^\d]", RegexOptions.Compiled);

        static long ParseVariantCount(string str)
        {
            string digits = s_NonDigitRx.Replace(str.Trim(), "");
            return long.TryParse(digits, out long n) ? n : 0L;
        }

        void EnsureLogTreeView()
        {
            if (m_LogTreeView != null) return;
            if (m_LogTreeState == null) m_LogTreeState = new TreeViewState<int>();
            var header    = new MultiColumnHeader(LogTreeView.CreateDefaultHeaderState());
            m_LogTreeView = new LogTreeView(m_LogTreeState, header);
        }

        void ApplyLogFilter()
        {
            m_LogFilteredEntries.Clear();
            if (string.IsNullOrEmpty(m_LogFilter))
                m_LogFilteredEntries.AddRange(m_LogEntries);
            else
                foreach (var e in m_LogEntries)
                    if (e.ShaderName.IndexOf(m_LogFilter, StringComparison.OrdinalIgnoreCase) >= 0
                     || e.PassName.IndexOf(  m_LogFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        m_LogFilteredEntries.Add(e);

            EnsureLogTreeView();
            m_LogTreeView.SetSource(m_LogFilteredEntries);
        }

        void DrawEditorLogTab()
        {
            // ── File picker ──────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Log File:", GUILayout.Width(58f));
            m_LogFilePath = EditorGUILayout.TextField(m_LogFilePath);
            if (GUILayout.Button("Browse…", GUILayout.Width(68f)))
            {
                string picked = EditorUtility.OpenFilePanel("Select Editor Log", "", "log,txt,");
                if (!string.IsNullOrEmpty(picked))
                    m_LogFilePath = picked;
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_LogFilePath)))
            {
                if (GUILayout.Button("Parse", GUILayout.Width(54f)))
                    ParseEditorLog(m_LogFilePath);
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(2f);

            if (!string.IsNullOrEmpty(m_LogStatusMsg))
                EditorGUILayout.HelpBox(m_LogStatusMsg, MessageType.Info);

            if (m_LogEntries.Count == 0) return;

            // ── Filter ───────────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(40f));
            string newFilter = EditorGUILayout.TextField(m_LogFilter);
            if (newFilter != m_LogFilter)
            {
                m_LogFilter = newFilter;
                ApplyLogFilter();
                GUI.FocusControl(null);
            }
            if (GUILayout.Button("✕", GUILayout.Width(22f)))
            {
                m_LogFilter = "";
                ApplyLogFilter();
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2f);

            // ── TreeView table ───────────────────────────────────────────────
            EnsureLogTreeView();
            float treeH  = Mathf.Max(50f, position.height - 150f);
            Rect treeRect = GUILayoutUtility.GetRect(ContentWidth(), treeH);
            m_LogTreeView.OnGUI(treeRect);

            // ── Footer ───────────────────────────────────────────────────────
            int shown = m_LogFilteredEntries.Count, total = m_LogEntries.Count;
            GUILayout.Label(
                shown == total ? $"{total} entries" : $"{shown} of {total} entries",
                EditorStyles.centeredGreyMiniLabel);
        }

        void ParseEditorLog(string path)
        {
            m_LogEntries.Clear();
            m_LogFilteredEntries.Clear();
            m_LogStatusMsg = string.Empty;

            if (!File.Exists(path))
            {
                m_LogStatusMsg = $"File not found: {path}";
                Repaint();
                return;
            }

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                m_LogStatusMsg = $"Could not read file: {ex.Message}";
                Repaint();
                return;
            }

            LogShaderEntry current          = null;
            int            detailsExpected   = 0;
            string         currentShaderName = string.Empty;

            const string k_StepToken      = "[Step ";
            const string k_ProcessedToken = "Processed in ";
            const string k_SecondsToken   = " seconds";

            foreach (string raw in lines)
            {
                // ── Fast hot-path routing via IndexOf (no regex on most lines) ──

                int stepIdx = raw.IndexOf(k_StepToken, StringComparison.Ordinal);
                if (stepIdx < 0)
                {
                    // "Compiling shader" can appear without any [Step] prefix.
                    int csIdx = raw.IndexOf("Compiling shader \"", StringComparison.Ordinal);
                    if (csIdx >= 0)
                    {
                        int nameStart = csIdx + 18; // len("Compiling shader \"")
                        int nameEnd   = raw.IndexOf('"', nameStart);
                        if (nameEnd > nameStart)
                            currentShaderName = raw.Substring(nameStart, nameEnd - nameStart);
                    }
                    continue;
                }

                // Locate the closing ']' of "[Step N/M]" and skip trailing whitespace.
                int closeBracket = raw.IndexOf(']', stepIdx + k_StepToken.Length);
                if (closeBracket < 0) continue;

                int contentStart = closeBracket + 1;
                while (contentStart < raw.Length && (raw[contentStart] == ' ' || raw[contentStart] == '\t'))
                    contentStart++;
                if (contentStart >= raw.Length) continue;

                string content = raw.Substring(contentStart);
                char   first   = content[0];

                // ── Dispatch by first character ─────────────────────────────────

                if (first == 'C' && content.StartsWith("Compiling shader \"", StringComparison.Ordinal))
                {
                    var m = s_LogCompilingShaderRx.Match(content);
                    if (m.Success) currentShaderName = m.Groups[1].Value;
                    continue;
                }

                if (first == 'S' && content.StartsWith("Shader=", StringComparison.Ordinal))
                {
                    var m = s_LogShaderLineRx.Match(content);
                    if (m.Success)
                    {
                        string full = m.Groups[1].Value.Trim();
                        string shaderName, passName;
                        if (!string.IsNullOrEmpty(currentShaderName)
                            && full.StartsWith(currentShaderName, StringComparison.Ordinal))
                        {
                            shaderName = currentShaderName;
                            passName   = full.Substring(currentShaderName.Length);
                        }
                        else
                        {
                            shaderName = full;
                            passName   = m.Groups[2].Value;
                        }

                        current = new LogShaderEntry
                        {
                            ShaderName       = shaderName,
                            PassName         = passName,
                            SubShaderIndex   = int.Parse(m.Groups[3].Value),
                            ShaderType       = m.Groups[4].Value,
                            Pipeline         = m.Groups[5].Value,
                            CompiledVariants = int.Parse(m.Groups[6].Value),
                            TotalVariants    = int.Parse(m.Groups[7].Value),
                            GraphicsAPI      = string.Empty,
                        };
                        m_LogEntries.Add(current);
                        detailsExpected = 5;
                    }
                    continue;
                }

                // Detail lines (Target / Full / After …)
                if (current == null || detailsExpected == 0) continue;

                switch (first)
                {
                    case 'T':
                        if (content.StartsWith("Target graphics API:", StringComparison.Ordinal))
                            current.GraphicsAPI = content.Substring(20).Trim();
                        break;
                    case 'F':
                        if (content.StartsWith("Full variant space:", StringComparison.Ordinal))
                            current.FullVariantSpace = ParseVariantCount(content.Substring(19));
                        break;
                    case 'A':
                        if (content.StartsWith("After settings filtering:", StringComparison.Ordinal))
                            current.AfterSettingsFilter = ParseVariantCount(content.Substring(25));
                        else if (content.StartsWith("After built-in stripping:", StringComparison.Ordinal))
                            current.AfterBuiltinStripping = ParseVariantCount(content.Substring(25));
                        else if (content.StartsWith("After scriptable stripping:", StringComparison.Ordinal))
                        {
                            current.AfterScriptableStripping = ParseVariantCount(content.Substring(27));
                            detailsExpected = 0;
                        }
                        break;
                    case 'P':
                        if (current.ProcessTimeSec == 0f
                            && content.StartsWith(k_ProcessedToken, StringComparison.Ordinal))
                        {
                            int numEnd = content.IndexOf(k_SecondsToken, k_ProcessedToken.Length,
                                                         StringComparison.Ordinal);
                            if (numEnd > k_ProcessedToken.Length)
                                float.TryParse(
                                    content.Substring(k_ProcessedToken.Length,
                                                      numEnd - k_ProcessedToken.Length),
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out current.ProcessTimeSec);
                        }
                        break;
                }
            }

            ApplyLogFilter();
            m_LogStatusMsg = $"Parsed {m_LogEntries.Count} shader compilation entries";
            Repaint();
        }

        // ── Column header helper ───────────────────────────────────────────────

        static void DrawColumnHeaders(
            string[] labels, float[] widths,
            ref int sortCol, ref bool sortAsc,
            Action onSorted)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            for (int i = 0; i < labels.Length; i++)
            {
                string indicator = i == sortCol ? (sortAsc ? " ▲" : " ▼") : "";
                if (GUILayout.Button(
                    labels[i] + indicator,
                    EditorStyles.toolbarButton,
                    GUILayout.Width(widths[i])))
                {
                    if (sortCol == i) sortAsc = !sortAsc;
                    else { sortCol = i; sortAsc = true; }
                    onSorted();
                    GUI.FocusControl(null);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Selectable row helper ──────────────────────────────────────────────

        static void DrawSelectableRow(
            int rowIdx, int selectedIdx, bool altRow,
            Action onClick, Action drawContent)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(k_RowH));
            GUILayout.Space(4f);
            drawContent();
            GUILayout.EndHorizontal();

            Rect rowRect = GUILayoutUtility.GetLastRect();

            bool isSelected = rowIdx == selectedIdx;
            if (isSelected)
                EditorGUI.DrawRect(rowRect, k_SelectColor);
            else if (altRow)
                EditorGUI.DrawRect(rowRect, k_AltRowColor);

            if (onClick != null && Event.current.type == EventType.MouseDown
                && rowRect.Contains(Event.current.mousePosition))
            {
                onClick();
                GUI.changed = true;
                Event.current.Use();
            }
        }

        // ── Analysis ──────────────────────────────────────────────────────────

        void Analyze()
        {
            m_SfKeywords.Clear();
            m_McKeywords.Clear();
            m_Permutations.Clear();
            m_SelectedKwIdx   = -1;
            m_SelectedMcIdx   = -1;
            m_SelectedPermIdx = -1;
            m_IsAnalyzed      = false;

            if (m_Shader == null)
            {
                m_StatusMessage = "No shader selected.";
                return;
            }

            try
            {
                // 1. Keyword universe: all non-dynamic keywords in keywordSpace.
                var allKwNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kw in m_Shader.keywordSpace.keywords)
                {
                    if (!string.IsNullOrEmpty(kw.name) && kw.name != "_" && !kw.isDynamic)
                        allKwNames.Add(kw.name);
                }

                // 2. Parse shader source + all reachable includes for pragma types.
                EditorUtility.DisplayProgressBar("Shader Variant Analyzer", "Parsing shader source…", 0.05f);
                var parsed = ParseShaderSource(m_Shader);

                // 3. Classify: MC wins if a keyword somehow appears in both pragmas.
                var mcKeywordNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (tokens, _, _) in parsed.McSets)
                    foreach (var opt in tokens)
                        if (opt != "_") mcKeywordNames.Add(opt);

                var sfSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in allKwNames)
                    if (parsed.SfLocations.ContainsKey(name) && !mcKeywordNames.Contains(name))
                        sfSet.Add(name);

                // 4. Build MC list: one row per unique pragma set from source.
                //    relevantOptions = only the options that actually exist in keywordSpace for
                //    this shader (Unity may have stripped others). The "_" off-state is preserved
                //    if it was present in the original pragma.
                //    The dedup key is sorted so "_|A|B" and "_|B|A" resolve to the same set.
                var seenSetKeys  = new HashSet<string>(StringComparer.Ordinal);
                var accountedKws = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (set, setFile, setLine) in parsed.McSets)
                {
                    var kwsInSpace = set.Where(o => o != "_" && allKwNames.Contains(o)).ToArray();
                    if (kwsInSpace.Length == 0) continue;

                    bool hasOff = set.Contains("_");
                    var relevantOptions = (hasOff ? new[] { "_" } : Array.Empty<string>())
                        .Concat(kwsInSpace)
                        .ToArray();

                    // Normalise order so the same set declared differently across files deduplicates.
                    string setKey = string.Join("|", relevantOptions.OrderBy(o => o, StringComparer.Ordinal));
                    if (!seenSetKeys.Add(setKey)) continue;

                    m_McKeywords.Add(new MultiCompileData
                    {
                        Name      = string.Join(" | ", kwsInSpace),
                        Options   = relevantOptions,
                        IsBuiltin = false,
                        FilePath  = setFile,
                        Line      = setLine,
                    });
                    foreach (var kw in kwsInSpace)
                        accountedKws.Add(kw);
                }

                // 5. Built-in keywords: in keywordSpace, not SF, not covered by any parsed pragma.
                foreach (var name in allKwNames)
                {
                    if (!sfSet.Contains(name) && !accountedKws.Contains(name))
                    {
                        m_McKeywords.Add(new MultiCompileData
                        {
                            Name      = name,
                            Options   = new[] { "_", name },
                            IsBuiltin = true,
                        });
                    }
                }

                // 6. Count shader passes.
                int passCount = 0;
                var shaderData = ShaderUtil.GetShaderData(m_Shader);
                for (int si = 0; si < shaderData.SubshaderCount; si++)
                    passCount += shaderData.GetSubshader(si).PassCount;
                passCount = Mathf.Max(passCount, 1);

                // 7. Scan all materials that use this shader.
                EditorUtility.DisplayProgressBar("Shader Variant Analyzer", "Finding materials…", 0.2f);
                string[] guids  = AssetDatabase.FindAssets("t:Material");
                var materials   = new List<Material>(guids.Length / 8);

                for (int g = 0; g < guids.Length; g++)
                {
                    if (g % 50 == 0)
                        EditorUtility.DisplayProgressBar(
                            "Shader Variant Analyzer",
                            $"Scanning materials… ({g}/{guids.Length})",
                            0.2f + 0.7f * g / guids.Length);

                    string path = AssetDatabase.GUIDToAssetPath(guids[g]);
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat != null && mat.shader == m_Shader)
                        materials.Add(mat);
                }

                // 8. Build permutation map keyed on the sorted SF keyword combination.
                EditorUtility.DisplayProgressBar("Shader Variant Analyzer", "Building permutations…", 0.92f);

                var permDict     = new Dictionary<string, PermutationData>(StringComparer.Ordinal);
                var kwPermCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var kwMatCounts  = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var mat in materials)
                {
                    string[] enabledKws = mat.enabledKeywords
                        .Select(k => k.name)
                        .Where(n => sfSet.Contains(n))
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToArray();

                    string hash = string.Join("|", enabledKws);

                    if (!permDict.TryGetValue(hash, out var perm))
                    {
                        perm = new PermutationData { Hash = hash, Keywords = enabledKws };
                        permDict[hash] = perm;
                        foreach (var k in enabledKws)
                        {
                            kwPermCounts.TryGetValue(k, out int n);
                            kwPermCounts[k] = n + 1;
                        }
                    }

                    perm.Materials.Add(mat);
                    foreach (var k in enabledKws)
                    {
                        kwMatCounts.TryGetValue(k, out int n);
                        kwMatCounts[k] = n + 1;
                    }
                }

                // 9. Populate SF keyword list.
                foreach (var kw in sfSet)
                {
                    kwPermCounts.TryGetValue(kw, out int pc);
                    kwMatCounts.TryGetValue(kw, out int mc);
                    parsed.SfLocations.TryGetValue(kw, out var loc);
                    m_SfKeywords.Add(new KeywordData
                    {
                        Name             = kw,
                        PermutationCount = pc,
                        MaterialCount    = mc,
                        FilePath         = loc.file,
                        Line             = loc.line,
                    });
                }

                m_Permutations.AddRange(permDict.Values);
                m_IsAnalyzed = true;
                SortAll();

                int userMcSets = m_McKeywords.Count(mc => !mc.IsBuiltin);

                m_StatusMessage =
                    $"{m_Shader.name}  ·  " +
                    $"{m_SfKeywords.Count} shader_feature kw  ·  " +
                    $"{userMcSets} user MC set(s)  ·  " +
                    $"{m_Permutations.Count} SF permutation(s)  ·  " +
                    $"{materials.Count} material(s)  ·  {passCount} pass(es)";
            }
            catch (Exception e)
            {
                m_StatusMessage = $"Error: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ── Sorting ───────────────────────────────────────────────────────────

        void SortAll()
        {
            SortSfKeywords();
            SortMcKeywords();
            SortPermutations();
        }

        void SortSfKeywords()
        {
            m_SfKeywords.Sort((a, b) =>
            {
                int cmp = m_KwSortCol switch
                {
                    0 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                    1 => a.PermutationCount.CompareTo(b.PermutationCount),
                    2 => a.MaterialCount.CompareTo(b.MaterialCount),
                    _ => 0
                };
                return m_KwSortAsc ? cmp : -cmp;
            });
        }

        void SortMcKeywords()
        {
            m_McKeywords.Sort((a, b) =>
            {
                int cmp = m_McSortCol switch
                {
                    0 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                    1 => a.OptionCount.CompareTo(b.OptionCount),
                    2 => a.IsBuiltin.CompareTo(b.IsBuiltin),
                    _ => 0
                };
                return m_McSortAsc ? cmp : -cmp;
            });
        }

        void SortPermutations()
        {
            m_Permutations.Sort((a, b) =>
            {
                int cmp = m_PermSortCol switch
                {
                    0 => string.Compare(a.KeywordsLabel, b.KeywordsLabel, StringComparison.OrdinalIgnoreCase),
                    1 => a.Materials.Count.CompareTo(b.Materials.Count),
                    _ => 0
                };
                return m_PermSortAsc ? cmp : -cmp;
            });
        }

        // ── Utility ───────────────────────────────────────────────────────────

        float ContentWidth() => position.width - 24f;
    }
}
