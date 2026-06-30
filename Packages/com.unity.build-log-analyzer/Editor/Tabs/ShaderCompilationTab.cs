using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BuildLogAnalyzer.Editor
{
    sealed class ShaderCompilationTab : BuildLogAnalyzerTab
    {
        // ── Data model ────────────────────────────────────────────────────────

        sealed class LogShaderEntry
        {
            public int    LineNumber;
            public int    LineNumberEnd;
            public string ShaderName;
            public string PassName;
            public string PassTag          = string.Empty;
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
            public float  FinishedTimeSec;
            public int    LocalCacheHits;
            public float  LocalCacheCpuSec;
            public int    RemoteCacheHits;
            public float  RemoteCacheCpuSec;
            public int    CompiledCount;
            public float  CompiledCpuSec;
            public int    SkippedCount;

            // Populated by warning analyzers after parsing
            public List<RowWarning> Warnings;
        }

        // ── Warning analyzers ─────────────────────────────────────────────────

        // Detects when settings filtering had no effect (Full Space == After Settings),
        // meaning no graphics API / platform filtering reduced the variant count.
        sealed class NoSettingsFilteringAnalyzer : IRowWarningAnalyzer<LogShaderEntry>
        {
            public void Analyze(LogShaderEntry entry, List<RowWarning> results)
            {
                if (entry.FullVariantSpace > 0 && entry.FullVariantSpace == entry.AfterSettingsFilter)
                    results.Add(new RowWarning(
                        $"Settings filtering had no effect — Full Space equals After Settings ({entry.FullVariantSpace:N0} variants). " +
                        "Ensure the SubShader has the correct RenderPipeline tag (e.g. \"UniversalPipeline\"), that the Pass LightingMode tag matches a URP pass type (e.g. \"UniversalForward\"), " +
                        "and that all keywords (shader_feature and multi_compile) are declared with the same names used in the URP Lit shader."));
            }
        }

        // ── TreeView ──────────────────────────────────────────────────────────

        sealed class LogTreeView : TreeView<int>
        {
            List<LogShaderEntry> m_Source = new List<LogShaderEntry>();

            // Column layout:
            //  0  Line
            //  1  ⚠ Warnings (icon + count)
            //  2  Shader
            //  3  Pass
            //  4  Stage
            //  5  API
            //  6  Full Space
            //  7  After Settings
            //  8  After Built-in
            //  9  After Scriptable
            // 10  Strip Time
            // 11  Compile Time
            // 12  Local Cache
            // 13  Remote Cache
            // 14  Compiled (CPU)

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

            public LogShaderEntry GetEntry(int sortedIndex)
                => (sortedIndex >= 0 && sortedIndex < m_Source.Count) ? m_Source[sortedIndex] : null;

            void SortSource()
            {
                int col = multiColumnHeader.sortedColumnIndex;
                if (col < 0 || m_Source.Count == 0) return;
                bool asc = multiColumnHeader.IsSortedAscending(col);
                m_Source.Sort((a, b) =>
                {
                    int cmp = col switch
                    {
                        0  => a.LineNumber.CompareTo(b.LineNumber),
                        1  => (a.Warnings?.Count ?? 0).CompareTo(b.Warnings?.Count ?? 0),
                        2  => string.Compare(a.ShaderName,  b.ShaderName,  StringComparison.OrdinalIgnoreCase),
                        3  => string.Compare(a.PassName,    b.PassName,    StringComparison.OrdinalIgnoreCase),
                        4  => string.Compare(a.ShaderType,  b.ShaderType,  StringComparison.OrdinalIgnoreCase),
                        5  => string.Compare(a.GraphicsAPI, b.GraphicsAPI, StringComparison.OrdinalIgnoreCase),
                        6  => a.FullVariantSpace.CompareTo(b.FullVariantSpace),
                        7  => a.AfterSettingsFilter.CompareTo(b.AfterSettingsFilter),
                        8  => a.AfterBuiltinStripping.CompareTo(b.AfterBuiltinStripping),
                        9  => a.AfterScriptableStripping.CompareTo(b.AfterScriptableStripping),
                        10 => a.ProcessTimeSec.CompareTo(b.ProcessTimeSec),
                        11 => a.FinishedTimeSec.CompareTo(b.FinishedTimeSec),
                        12 => a.LocalCacheHits.CompareTo(b.LocalCacheHits),
                        13 => a.RemoteCacheHits.CompareTo(b.RemoteCacheHits),
                        14 => a.CompiledCount.CompareTo(b.CompiledCount),
                        _  => 0
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

                var shader = Shader.Find(shaderName);
                if (shader != null) { PingAndSelect(shader); return; }

                string hint = shaderName.Contains('/')
                    ? shaderName.Substring(shaderName.LastIndexOf('/') + 1)
                    : shaderName;

                foreach (string guid in AssetDatabase.FindAssets(hint))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var asShader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (asShader != null && asShader.name == shaderName) { PingAndSelect(asShader); return; }
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
                    int col = args.GetColumn(i);

                    if (col == 1)
                    {
                        int wc = e.Warnings?.Count ?? 0;
                        if (wc > 0)
                        {
                            var icon = EditorGUIUtility.IconContent("console.warnicon.inactive.sml");
                            EditorGUI.LabelField(rect, new GUIContent($" {wc}", icon.image, $"{wc} warning(s)"));
                        }
                        continue;
                    }

                    string text = col switch
                    {
                        0  => e.LineNumberEnd > 0 ? $"{e.LineNumber}–{e.LineNumberEnd}" : e.LineNumber.ToString(),
                        2  => e.ShaderName,
                        3  => e.PassName,
                        4  => e.ShaderType,
                        5  => e.GraphicsAPI,
                        6  => e.FullVariantSpace.ToString("N", s_DotGroupFmt),
                        7  => e.AfterSettingsFilter.ToString("N", s_DotGroupFmt),
                        8  => e.AfterBuiltinStripping.ToString("N", s_DotGroupFmt),
                        9  => e.AfterScriptableStripping.ToString("N", s_DotGroupFmt),
                        10 => e.ProcessTimeSec > 0f ? e.ProcessTimeSec.ToString("F2") : "—",
                        11 => e.FinishedTimeSec.ToString("F2"),
                        12 => $"{e.LocalCacheHits} ({e.LocalCacheCpuSec:F2}s)",
                        13 => $"{e.RemoteCacheHits} ({e.RemoteCacheCpuSec:F2}s)",
                        14 => $"{e.CompiledCount} ({e.CompiledCpuSec:F2}s)",
                        _  => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",           "Log file line range for this shader pass (start–end)"),                                                         width = 90,  minWidth = 55,  autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("⚠",              "Number of warnings detected for this entry"),                                                                   width = 30,  minWidth = 25,  autoResize = false, canSort = true, allowToggleVisibility = true  },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Shader"),         width = 200, minWidth = 80,  autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Pass"),           width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Stage"),          width = 55,  minWidth = 40,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("API"),            width = 50,  minWidth = 40,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Full Space"),     width = 80,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Settings"), width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Built-in"), width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Scriptable"), width = 100, minWidth = 50, autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Strip Time (s)",    "Time spent stripping variants (Processed in X seconds)"),                                                    width = 75,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Compile Time (s)",  "Total wall-clock time to compile this pass (finished in X seconds)"),                                        width = 75,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Local Cache",       "Variants served from local cache: hit count and CPU time spent on cache lookups"),                           width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Remote Cache",      "Variants served from remote cache: hit count and CPU time spent on remote cache lookups"),                   width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Compiled (CPU)",    "Variants compiled from source: count and cumulative CPU time across all compiler threads (can exceed wall time when parallel compilation is active)"), width = 110, minWidth = 60, autoResize = false, canSort = true },
                });
                state.sortedColumnIndex          = 6;
                state.columns[6].sortedAscending = false;
                return state;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<LogShaderEntry> m_Entries         = new List<LogShaderEntry>();
        readonly List<LogShaderEntry> m_FilteredEntries = new List<LogShaderEntry>();
        string             m_StatusMsg  = string.Empty;
        string             m_ParseExtra = string.Empty;
        string             m_Filter     = string.Empty;
        LogTreeView        m_TreeView;
        TreeViewState<int> m_TreeState;

        LogShaderEntry m_Selected;
        Vector2        m_DetailScroll;
        float          m_DetailPanelHeight = 120f;
        bool           m_Resizing;
        EditorWindow   m_Window;

        // Add new analyzers here to extend warning detection for this tab.
        readonly List<IRowWarningAnalyzer<LogShaderEntry>> m_WarningAnalyzers = new List<IRowWarningAnalyzer<LogShaderEntry>>
        {
            new NoSettingsFilteringAnalyzer(),
        };

        // ── Regexes & format helpers ──────────────────────────────────────────

        static readonly System.Globalization.NumberFormatInfo s_DotGroupFmt =
            new System.Globalization.NumberFormatInfo
            {
                NumberGroupSeparator = ".",
                NumberGroupSizes     = new[] { 3 },
                NumberDecimalDigits  = 0,
            };

        static readonly Regex s_LogShaderLineRx = new Regex(
            @"Shader=(.+?)\s+\((\w[\w\s]*?)\)\s+\(SubShader:\s*(\d+)\)\s+\(ShaderType:\s*(\w+)\)\s+Pipeline=(\S*)\s+Total=(\d+)/(\d+)\([^)]+\)\s+Time=([\d.]+)ms",
            RegexOptions.Compiled);

        static readonly Regex s_LogFinishedRx = new Regex(
            @"Pass\s+(.*?)\s*\((\w+),\s*(\w+)\)\s+finished in ([\d.]+) seconds\.\s+" +
            @"Local cache hits (\d+) \(([\d.]+)s CPU time\), " +
            @"remote cache hits (\d+) \(([\d.]+)s CPU time\), " +
            @"compiled (\d+) variants \(([\d.]+)s CPU time\), " +
            @"skipped (\d+) variants",
            RegexOptions.Compiled);

        static readonly Regex s_NonDigitRx = new Regex(@"[^\d]", RegexOptions.Compiled);

        // ── BuildLogAnalyzerTab ───────────────────────────────────────────────

        public override string TabName => "Shader Compilation";

        public override void OnEnable(EditorWindow window) => m_Window = window;

        public override void Clear()
        {
            m_Entries.Clear();
            m_FilteredEntries.Clear();
            m_StatusMsg = string.Empty;
            m_Selected  = null;
        }

        public override string GetStatusMessage() => m_StatusMsg;

        public override void ParseLines(string[] lines)
        {
            LogShaderEntry current          = null;
            bool           currentCommitted  = false;
            string         currentShaderName = string.Empty;
            string         currentPassName   = string.Empty;
            int            currentStartLine  = 0;

            const int k_CompilingShaderLen  = 18;
            const int k_PassQuoteLen        = 6;
            const int k_FullVariantSpaceLen = 19;
            const int k_AfterSettingsLen    = 25;
            const int k_AfterBuiltinLen     = 25;
            const int k_AfterScriptableLen  = 27;
            const int k_ProcessedInLen      = 13;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string raw = lines[lineIdx];
                int idx;

                idx = raw.IndexOf("Compiling shader \"", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int nameStart = idx + k_CompilingShaderLen;
                    int nameEnd   = raw.IndexOf('"', nameStart);
                    if (nameEnd > nameStart)
                        currentShaderName = raw.Substring(nameStart, nameEnd - nameStart);
                    currentPassName = string.Empty;
                    continue;
                }

                idx = raw.IndexOf("Pass \"", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int nameStart = idx + k_PassQuoteLen;
                    int nameEnd   = raw.IndexOf('"', nameStart);
                    if (nameEnd >= nameStart)
                        currentPassName = raw.Substring(nameStart, nameEnd - nameStart);
                    continue;
                }

                idx = raw.IndexOf("Shader=", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var m = s_LogShaderLineRx.Match(raw, idx);
                    if (m.Success)
                    {
                        string full = m.Groups[1].Value.Trim();
                        string shaderName, passName;

                        if (!string.IsNullOrEmpty(currentPassName)
                            && full.EndsWith(currentPassName, StringComparison.Ordinal))
                        {
                            shaderName = full.Substring(0, full.Length - currentPassName.Length);
                            passName   = currentPassName;
                        }
                        else if (!string.IsNullOrEmpty(currentShaderName)
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
                            PassTag          = currentPassName,
                            SubShaderIndex   = int.Parse(m.Groups[3].Value),
                            ShaderType       = m.Groups[4].Value,
                            Pipeline         = m.Groups[5].Value,
                            CompiledVariants = int.Parse(m.Groups[6].Value),
                            TotalVariants    = int.Parse(m.Groups[7].Value),
                            GraphicsAPI      = string.Empty,
                        };
                        currentStartLine = lineIdx + 1;
                        currentCommitted = false;
                    }
                    continue;
                }

                idx = raw.IndexOf("Local cache hits ", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var mf = s_LogFinishedRx.Match(raw);
                    if (mf.Success)
                    {
                        string finPassName = mf.Groups[1].Value.Trim();
                        string shaderType  = StageAbbrevToShaderType(mf.Groups[2].Value);
                        float.TryParse(mf.Groups[4].Value,  System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float finSec);
                        int.TryParse  (mf.Groups[5].Value,  out int   localHits);
                        float.TryParse(mf.Groups[6].Value,  System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float localCpu);
                        int.TryParse  (mf.Groups[7].Value,  out int   remoteHits);
                        float.TryParse(mf.Groups[8].Value,  System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float remoteCpu);
                        int.TryParse  (mf.Groups[9].Value,  out int   compiledCount);
                        float.TryParse(mf.Groups[10].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float compiledCpu);
                        int.TryParse  (mf.Groups[11].Value, out int   skippedCount);

                        for (int i = m_Entries.Count - 1; i >= 0; i--)
                        {
                            var e = m_Entries[i];
                            bool passMatch = string.IsNullOrEmpty(finPassName)
                                ? string.IsNullOrEmpty(e.PassTag)
                                : e.PassTag == finPassName;
                            if (passMatch
                                && e.ShaderType == shaderType
                                && (string.IsNullOrEmpty(currentShaderName) || e.ShaderName == currentShaderName))
                            {
                                e.LineNumberEnd     = lineIdx + 1;
                                e.FinishedTimeSec   = finSec;
                                e.LocalCacheHits    = localHits;
                                e.LocalCacheCpuSec  = localCpu;
                                e.RemoteCacheHits   = remoteHits;
                                e.RemoteCacheCpuSec = remoteCpu;
                                e.CompiledCount     = compiledCount;
                                e.CompiledCpuSec    = compiledCpu;
                                e.SkippedCount      = skippedCount;
                                break;
                            }
                        }
                    }
                    continue;
                }

                if (current == null) continue;

                idx = raw.IndexOf("Target graphics API:", StringComparison.Ordinal);
                if (idx >= 0) { current.GraphicsAPI = raw.Substring(idx + 20).Trim(); continue; }

                idx = raw.IndexOf("Full variant space:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    if (!currentCommitted)
                    {
                        current.LineNumber = currentStartLine;
                        m_Entries.Add(current);
                        currentCommitted = true;
                    }
                    current.FullVariantSpace = ParseVariantCount(raw.Substring(idx + k_FullVariantSpaceLen));
                    continue;
                }

                idx = raw.IndexOf("After settings filtering:", StringComparison.Ordinal);
                if (idx >= 0) { current.AfterSettingsFilter = ParseVariantCount(raw.Substring(idx + k_AfterSettingsLen)); continue; }

                idx = raw.IndexOf("After built-in stripping:", StringComparison.Ordinal);
                if (idx >= 0) { current.AfterBuiltinStripping = ParseVariantCount(raw.Substring(idx + k_AfterBuiltinLen)); continue; }

                idx = raw.IndexOf("After scriptable stripping:", StringComparison.Ordinal);
                if (idx >= 0) { current.AfterScriptableStripping = ParseVariantCount(raw.Substring(idx + k_AfterScriptableLen)); continue; }

                idx = raw.IndexOf("Processed in ", StringComparison.Ordinal);
                if (idx >= 0 && current.ProcessTimeSec == 0f)
                {
                    int numStart = idx + k_ProcessedInLen;
                    int numEnd   = raw.IndexOf(" seconds", numStart, StringComparison.Ordinal);
                    if (numEnd > numStart)
                        float.TryParse(raw.Substring(numStart, numEnd - numStart),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out current.ProcessTimeSec);
                    continue;
                }
            }

            ComputeWarnings();

            float totalStrip = 0f, totalCompile = 0f;
            foreach (var e in m_Entries) { totalStrip += e.ProcessTimeSec; totalCompile += e.FinishedTimeSec; }
            m_ParseExtra = $"  |  Total Strip: {FormatDuration(totalStrip)}  |  Total Compile: {FormatDuration(totalCompile)}";
            ApplyFilter();
        }

        public override void DrawGUI(float contentWidth)
        {
            if (m_Entries.Count == 0)
            {
                GUILayout.Label("No shader compilation entries found.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(40f));
            string newFilter = EditorGUILayout.TextField(m_Filter);
            if (newFilter != m_Filter) { m_Filter = newFilter; ApplyFilter(); }
            if (GUILayout.Button("✕", GUILayout.Width(22f))) { m_Filter = ""; ApplyFilter(); GUI.FocusControl(null); }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2f);

            EnsureTreeView();

            Rect treeRect    = GUILayoutUtility.GetRect(contentWidth, 50f, GUILayout.ExpandHeight(true));
            Rect resizerRect = GUILayoutUtility.GetRect(contentWidth, 5f,  GUILayout.Height(5f));
            Rect detailRect  = GUILayoutUtility.GetRect(contentWidth, m_DetailPanelHeight, GUILayout.Height(m_DetailPanelHeight));

            m_TreeView.OnGUI(treeRect);

            // Poll selection each frame — avoids dependence on event callbacks.
            var sel = m_TreeView.GetSelection();
            var newSelected = sel.Count > 0 ? m_TreeView.GetEntry(sel[0]) : null;
            if (newSelected != m_Selected) { m_Selected = newSelected; m_DetailScroll = Vector2.zero; }

            EditorGUI.DrawRect(resizerRect, new Color(0f, 0f, 0f, 0.2f));
            EditorGUIUtility.AddCursorRect(resizerRect, MouseCursor.ResizeVertical);
            HandleSplitterDrag(resizerRect);

            DrawDetails(detailRect);
        }

        // ── Details panel ─────────────────────────────────────────────────────

        void DrawDetails(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            if (m_Selected == null)
            {
                GUI.Label(rect, "Select a row to see warnings and details.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var inner = new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8);

            float lh       = EditorGUIUtility.singleLineHeight + 2f;
            var   warnings = m_Selected.Warnings;
            int   wc       = warnings?.Count ?? 0;

            // Pre-compute content height for the scroll view.
            float contentH = lh + 4f; // entry header
            if (wc == 0)
            {
                contentH += lh;
            }
            else
            {
                contentH += lh; // "N Warning(s)" label
                var wrapStyle = EditorStyles.wordWrappedMiniLabel;
                float wrapWidth = Mathf.Max(inner.width - 32f, 100f);
                foreach (var w in warnings)
                    contentH += wrapStyle.CalcHeight(new GUIContent($"• {w.Message}"), wrapWidth) + 2f;
            }

            var contentRect = new Rect(0, 0, inner.width - 16f, Mathf.Max(contentH, inner.height));
            m_DetailScroll  = GUI.BeginScrollView(inner, m_DetailScroll, contentRect);

            float y = 0f;

            // Entry header
            string headerText = m_Selected.ShaderName;
            if (!string.IsNullOrEmpty(m_Selected.PassName))   headerText += $"  —  {m_Selected.PassName.Trim()}";
            if (!string.IsNullOrEmpty(m_Selected.ShaderType)) headerText += $"  [{m_Selected.ShaderType}]";
            if (!string.IsNullOrEmpty(m_Selected.GraphicsAPI)) headerText += $"  {m_Selected.GraphicsAPI}";
            GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight), headerText, EditorStyles.boldLabel);
            y += lh + 2f;

            // Warnings section
            if (wc == 0)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "No warnings detected.", EditorStyles.miniLabel);
            }
            else
            {
                var warnIcon = EditorGUIUtility.IconContent("console.warnicon.inactive.sml");
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    new GUIContent($" {wc} Warning{(wc > 1 ? "s" : "")}", warnIcon.image),
                    EditorStyles.boldLabel);
                y += lh;

                var wrapStyle = EditorStyles.wordWrappedMiniLabel;
                float wrapWidth = Mathf.Max(contentRect.width - 20f, 100f);
                foreach (var w in warnings)
                {
                    string msg     = $"• {w.Message}";
                    float  msgH    = wrapStyle.CalcHeight(new GUIContent(msg), wrapWidth);
                    GUI.Label(new Rect(8, y, wrapWidth, msgH), msg, wrapStyle);
                    y += msgH + 2f;
                }
            }

            GUI.EndScrollView();
        }

        void HandleSplitterDrag(Rect resizerRect)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && resizerRect.Contains(e.mousePosition))
            { m_Resizing = true; e.Use(); return; }
            if (m_Resizing && e.type == EventType.MouseDrag)
            {
                m_DetailPanelHeight = Mathf.Clamp(m_DetailPanelHeight - e.delta.y, 40f, 500f);
                m_Window?.Repaint();
                e.Use();
            }
            if (m_Resizing && e.type == EventType.MouseUp)
            { m_Resizing = false; e.Use(); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        void ComputeWarnings()
        {
            foreach (var entry in m_Entries)
            {
                var results = new List<RowWarning>();
                foreach (var analyzer in m_WarningAnalyzers)
                    analyzer.Analyze(entry, results);
                entry.Warnings = results.Count > 0 ? results : null;
            }
        }

        void ApplyFilter()
        {
            m_FilteredEntries.Clear();
            if (string.IsNullOrEmpty(m_Filter))
                m_FilteredEntries.AddRange(m_Entries);
            else
                foreach (var e in m_Entries)
                    if (e.ShaderName.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0
                     || e.PassName.IndexOf(  m_Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        m_FilteredEntries.Add(e);

            EnsureTreeView();
            m_TreeView.SetSource(m_FilteredEntries);

            int shown = m_FilteredEntries.Count, total = m_Entries.Count;
            m_StatusMsg = shown == total
                ? $"Showing {total} entries{m_ParseExtra}"
                : $"Showing {shown} of {total} entries{m_ParseExtra}";
        }

        void EnsureTreeView()
        {
            if (m_TreeView != null) return;
            if (m_TreeState == null) m_TreeState = new TreeViewState<int>();
            m_TreeView = new LogTreeView(m_TreeState, new MultiColumnHeader(LogTreeView.CreateDefaultHeaderState()));
        }

        static string StageAbbrevToShaderType(string abbrev) => abbrev switch
        {
            "vp" => "Vertex",
            "fp" => "Fragment",
            "gp" => "Geometry",
            "hp" => "Hull",
            "dp" => "Domain",
            _    => abbrev,
        };

        static string FormatDuration(float seconds)
        {
            int totalSec = Mathf.FloorToInt(seconds);
            if (totalSec < 60) return $"{seconds:F3}s";
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            return h > 0 ? $"{h}h {m}m {s}s" : $"{m}m {s}s";
        }

        static long ParseVariantCount(string str)
        {
            string digits = s_NonDigitRx.Replace(str.Trim(), "");
            return long.TryParse(digits, out long n) ? n : 0L;
        }
    }
}
