using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BuildLogAnalyzer.Editor
{
    sealed class ScriptRecompilationTab : BuildLogAnalyzerTab
    {
        // ── Data model ────────────────────────────────────────────────────────

        sealed class TundraSuccess
        {
            public int   LineNumber;
            public float TimeSec;
            public int   ItemsUpdated;
            public int   ItemsEvaluated;
        }

        sealed class RecompilationEntry
        {
            public int               LineNumber;
            public List<string>      Reasons         = new List<string>();
            public List<TundraSuccess> TundraBuilds  = new List<TundraSuccess>();
            public float             TotalTimeSec;

            public string ReasonsDisplay =>
                Reasons.Count == 0 ? "(no reasons captured)" : string.Join(" | ", Reasons);
        }

        sealed class RecompilationTreeView : TreeView<int>
        {
            List<RecompilationEntry> m_Source = new List<RecompilationEntry>();

            public RecompilationTreeView(TreeViewState<int> state, MultiColumnHeader header) : base(state, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<RecompilationEntry> entries)
            {
                m_Source = entries;
                SortSource();
                Reload();
            }

            public RecompilationEntry GetEntry(int sortedIndex)
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
                        0 => a.LineNumber.CompareTo(b.LineNumber),
                        1 => string.Compare(a.ReasonsDisplay, b.ReasonsDisplay, StringComparison.OrdinalIgnoreCase),
                        2 => a.TotalTimeSec.CompareTo(b.TotalTimeSec),
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
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].ReasonsDisplay));
                SetupParentsAndChildrenFromDepths(root, items);
                return root;
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
                        0 => e.LineNumber.ToString(),
                        1 => e.ReasonsDisplay,
                        2 => e.TotalTimeSec > 0f ? e.TotalTimeSec.ToString("F2") : "—",
                        _ => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",           "Log file line where the Script compilation block started"),                                          width = 55,  minWidth = 40,  autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Reasons",        "Reasons that triggered this script compilation"),                                                    width = 380, minWidth = 100, autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Total Time (s)", "Cumulative time of all Tundra build success entries associated with this recompilation"),           width = 100, minWidth = 60,  autoResize = false, canSort = true },
                });
                state.sortedColumnIndex          = 0;
                state.columns[0].sortedAscending = true;
                return state;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<RecompilationEntry> m_Entries         = new List<RecompilationEntry>();
        readonly List<RecompilationEntry> m_FilteredEntries = new List<RecompilationEntry>();
        string                 m_StatusMsg         = string.Empty;
        string                 m_ParseExtra        = string.Empty;
        string                 m_Filter            = string.Empty;
        RecompilationTreeView  m_TreeView;
        TreeViewState<int>     m_TreeState;
        RecompilationEntry     m_Selected;
        Vector2                m_DetailScroll;
        float                  m_DetailPanelHeight = 150f;
        bool                   m_Resizing;
        EditorWindow           m_Window;

        // ── Regexes ───────────────────────────────────────────────────────────

        // "*** Tundra build success (0.40 seconds), 1 items updated, 2177 evaluated"
        static readonly Regex s_TundraRx = new Regex(
            @"\*\*\* Tundra build success \(([\d.]+) seconds\),\s*(\d+) items updated,\s*(\d+) evaluated",
            RegexOptions.Compiled);

        // ── BuildLogAnalyzerTab ───────────────────────────────────────────────

        public override string TabName => "Script Recompilations";

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
            RecompilationEntry current = null;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string raw = lines[lineIdx];

                // Detect the opening "Script compilation" header (not a reason line).
                // Reason lines look like "[Script compilation] some reason".
                // The header is a line that contains "Script compilation" but does NOT
                // start with "[Script compilation]".
                if (raw.IndexOf("Script compilation", StringComparison.Ordinal) >= 0
                    && raw.IndexOf("[Script compilation]", StringComparison.Ordinal) < 0)
                {
                    current = new RecompilationEntry
                    {
                        LineNumber = lineIdx + 1,
                    };
                    m_Entries.Add(current);
                    continue;
                }

                // Collect reason lines associated with the current compilation.
                if (current != null)
                {
                    int reasonIdx = raw.IndexOf("[Script compilation]", StringComparison.Ordinal);
                    if (reasonIdx >= 0)
                    {
                        string reason = raw.Substring(reasonIdx + "[Script compilation]".Length).Trim();
                        if (!string.IsNullOrEmpty(reason))
                            current.Reasons.Add(reason);
                        continue;
                    }
                }

                // Associate Tundra success with the most recent compilation.
                if (current != null && raw.IndexOf("Tundra build success", StringComparison.Ordinal) >= 0)
                {
                    var m = s_TundraRx.Match(raw);
                    if (m.Success
                        && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sec)
                        && int.TryParse(m.Groups[2].Value, out int updated)
                        && int.TryParse(m.Groups[3].Value, out int evaluated))
                    {
                        current.TundraBuilds.Add(new TundraSuccess
                        {
                            LineNumber     = lineIdx + 1,
                            TimeSec        = sec,
                            ItemsUpdated   = updated,
                            ItemsEvaluated = evaluated,
                        });
                        current.TotalTimeSec += sec;
                    }
                }
            }

            float grandTotal = 0f;
            foreach (var e in m_Entries) grandTotal += e.TotalTimeSec;
            m_ParseExtra = $"  |  Total Recompile Time: {FormatDuration(grandTotal)}";
            ApplyFilter();
        }

        public override void DrawGUI(float contentWidth)
        {
            if (m_Entries.Count == 0)
            {
                GUILayout.Label("No script recompilation entries found.", EditorStyles.centeredGreyMiniLabel);
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

            var sel         = m_TreeView.GetSelection();
            var newSelected = sel.Count > 0 ? m_TreeView.GetEntry(sel[0]) : null;
            if (newSelected != m_Selected) { m_Selected = newSelected; m_DetailScroll = Vector2.zero; }

            EditorGUI.DrawRect(resizerRect, new Color(0f, 0f, 0f, 0.2f));
            EditorGUIUtility.AddCursorRect(resizerRect, MouseCursor.ResizeVertical);
            HandleSplitterDrag(resizerRect);

            DrawDetails(detailRect);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        void DrawDetails(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            if (m_Selected == null)
            {
                GUI.Label(rect, "Select a recompilation to see its Tundra build details.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var   inner = new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8);
            float lh    = EditorGUIUtility.singleLineHeight + 2f;

            // Header line + reason lines + separator + one row per tundra build.
            int   lineCount   = 1 + m_Selected.Reasons.Count + 1 + Mathf.Max(1, m_Selected.TundraBuilds.Count);
            float contentH    = lineCount * lh + 8f;
            var   contentRect = new Rect(0, 0, inner.width - 16f, Mathf.Max(contentH, inner.height));

            m_DetailScroll = GUI.BeginScrollView(inner, m_DetailScroll, contentRect);
            float y = 0f;

            // Header
            string headerText = m_Selected.TundraBuilds.Count == 0
                ? $"Line {m_Selected.LineNumber}  —  no Tundra builds captured"
                : $"Line {m_Selected.LineNumber}  —  {m_Selected.TundraBuilds.Count} Tundra build(s)  |  Total: {m_Selected.TotalTimeSec:F2}s";
            GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight), headerText, EditorStyles.boldLabel);
            y += lh;

            // Reasons
            foreach (string reason in m_Selected.Reasons)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    $"  Reason: {reason}", EditorStyles.miniLabel);
                y += lh;
            }
            y += 2f;

            // Tundra build list
            if (m_Selected.TundraBuilds.Count == 0)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "  No Tundra build success entries found.", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var t in m_Selected.TundraBuilds)
                {
                    GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                        $"  Line {t.LineNumber}  —  {t.TimeSec:F2}s  ({t.ItemsUpdated} updated, {t.ItemsEvaluated} evaluated)",
                        EditorStyles.miniLabel);
                    y += lh;
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

        void ApplyFilter()
        {
            m_FilteredEntries.Clear();
            if (string.IsNullOrEmpty(m_Filter))
                m_FilteredEntries.AddRange(m_Entries);
            else
                foreach (var e in m_Entries)
                    if (e.ReasonsDisplay.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        m_FilteredEntries.Add(e);

            EnsureTreeView();
            m_TreeView.SetSource(m_FilteredEntries);

            int shown = m_FilteredEntries.Count, total = m_Entries.Count;
            m_StatusMsg = shown == total
                ? $"Showing {total} recompilation(s){m_ParseExtra}"
                : $"Showing {shown} of {total} recompilation(s){m_ParseExtra}";
        }

        void EnsureTreeView()
        {
            if (m_TreeView != null) return;
            if (m_TreeState == null) m_TreeState = new TreeViewState<int>();
            m_TreeView = new RecompilationTreeView(m_TreeState, new MultiColumnHeader(RecompilationTreeView.CreateDefaultHeaderState()));
        }

        static string FormatDuration(float seconds)
        {
            int totalSec = Mathf.FloorToInt(seconds);
            if (totalSec < 60) return $"{seconds:F3}s";
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            return h > 0 ? $"{h}h {m}m {s}s" : $"{m}m {s}s";
        }
    }
}
