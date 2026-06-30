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
                        0  => a.LineNumber.CompareTo(b.LineNumber),
                        1  => string.Compare(a.ShaderName,  b.ShaderName,  StringComparison.OrdinalIgnoreCase),
                        2  => string.Compare(a.PassName,    b.PassName,    StringComparison.OrdinalIgnoreCase),
                        3  => string.Compare(a.ShaderType,  b.ShaderType,  StringComparison.OrdinalIgnoreCase),
                        4  => string.Compare(a.GraphicsAPI, b.GraphicsAPI, StringComparison.OrdinalIgnoreCase),
                        5  => a.FullVariantSpace.CompareTo(b.FullVariantSpace),
                        6  => a.AfterSettingsFilter.CompareTo(b.AfterSettingsFilter),
                        7  => a.AfterBuiltinStripping.CompareTo(b.AfterBuiltinStripping),
                        8  => a.AfterScriptableStripping.CompareTo(b.AfterScriptableStripping),
                        9  => a.ProcessTimeSec.CompareTo(b.ProcessTimeSec),
                        10 => a.FinishedTimeSec.CompareTo(b.FinishedTimeSec),
                        11 => a.LocalCacheHits.CompareTo(b.LocalCacheHits),
                        12 => a.RemoteCacheHits.CompareTo(b.RemoteCacheHits),
                        13 => a.CompiledCount.CompareTo(b.CompiledCount),
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
                    string text = args.GetColumn(i) switch
                    {
                        0  => e.LineNumber.ToString(),
                        1  => e.ShaderName,
                        2  => e.PassName,
                        3  => e.ShaderType,
                        4  => e.GraphicsAPI,
                        5  => e.FullVariantSpace.ToString("N", s_DotGroupFmt),
                        6  => e.AfterSettingsFilter.ToString("N", s_DotGroupFmt),
                        7  => e.AfterBuiltinStripping.ToString("N", s_DotGroupFmt),
                        8  => e.AfterScriptableStripping.ToString("N", s_DotGroupFmt),
                        9  => e.ProcessTimeSec > 0f ? e.ProcessTimeSec.ToString("F2") : "—",
                        10 => e.FinishedTimeSec.ToString("F2"),
                        11 => $"{e.LocalCacheHits} ({e.LocalCacheCpuSec:F2}s)",
                        12 => $"{e.RemoteCacheHits} ({e.RemoteCacheCpuSec:F2}s)",
                        13 => $"{e.CompiledCount} ({e.CompiledCpuSec:F2}s)",
                        _  => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",  "Log file line number"),                                                                                        width = 55,  minWidth = 40,  autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Shader"),          width = 200, minWidth = 80,  autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Pass"),             width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Stage"),            width = 55,  minWidth = 40,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("API"),              width = 50,  minWidth = 40,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Full Space"),       width = 80,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Settings"),   width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Built-in"),   width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("After Scriptable"), width = 100, minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Strip Time (s)",  "Time spent stripping variants (Processed in X seconds)"),                                          width = 75,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Compile Time (s)", "Total wall-clock time to compile this pass (finished in X seconds)"),                               width = 75,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Local Cache",       "Variants served from local cache: hit count and CPU time spent on cache lookups"),                  width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Remote Cache",      "Variants served from remote cache: hit count and CPU time spent on remote cache lookups"),          width = 90,  minWidth = 50,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Compiled (CPU)",    "Variants compiled from source: count and cumulative CPU time across all compiler threads (can exceed wall time when parallel compilation is active)"), width = 110, minWidth = 60,  autoResize = false, canSort = true },
                });
                state.sortedColumnIndex          = 5;
                state.columns[5].sortedAscending = false;
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

        public override void Clear()
        {
            m_Entries.Clear();
            m_FilteredEntries.Clear();
            m_StatusMsg = string.Empty;
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
            Rect treeRect = GUILayoutUtility.GetRect(contentWidth, 50f, GUILayout.ExpandHeight(true));
            m_TreeView.OnGUI(treeRect);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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
