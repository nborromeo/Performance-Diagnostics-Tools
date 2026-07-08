using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BuildLogAnalyzer.Editor
{
    /// <summary>
    /// Aggregates the entries reported by every other tab into a single chronological
    /// table — line, source tab, name and time taken — so the order in which processes
    /// ran during the build can be seen at a glance. Clicking a name jumps to the entry's
    /// origin tab and selects it there.
    /// </summary>
    sealed class SummaryTab : BuildLogAnalyzerTab
    {
        sealed class SummaryTreeView : TreeView<int>
        {
            List<SummaryRow> m_Source = new List<SummaryRow>();
            public Action<SummaryRow> OnNameClicked;

            public SummaryTreeView(TreeViewState<int> state, MultiColumnHeader header) : base(state, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<SummaryRow> entries)
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
                        0 => a.LineNumber.CompareTo(b.LineNumber),
                        1 => string.Compare(a.Category,    b.Category,    StringComparison.OrdinalIgnoreCase),
                        2 => string.Compare(a.Name,        b.Name,        StringComparison.OrdinalIgnoreCase),
                        3 => a.DurationSec.CompareTo(b.DurationSec),
                        4 => a.LineNumber.CompareTo(b.LineNumber), // chronological == line order; survives midnight rollover
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
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].Name));
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
                    int col = args.GetColumn(i);

                    if (col == 0)
                    {
                        LogFileNavigator.DrawLineCell(rect, e.LineNumber, e.LineNumberEnd);
                        continue;
                    }

                    if (col == 2)
                    {
                        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                        if (GUI.Button(rect, new GUIContent(e.Name, "Jump to this entry"), EditorStyles.linkLabel))
                            OnNameClicked?.Invoke(e);
                        continue;
                    }

                    if (col == 4)
                    {
                        LogFileNavigator.DrawTimestampCell(rect, e.LineNumber, e.LineNumberEnd);
                        continue;
                    }

                    string text = col switch
                    {
                        1 => e.Category,
                        3 => e.DurationSec > 0f ? e.DurationSec.ToString("F2") : "—",
                        _ => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var columns = new List<MultiColumnHeaderState.Column>
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",     "Log file line range of this entry (start–end)"), width = 90,  minWidth = 55,  autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Tab",       "Which tab this entry came from"),                width = 130, minWidth = 80,  autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Name",      "Click to jump to this entry in its tab"),        width = 260, minWidth = 100, autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Time (s)",  "Time taken by this process"),                    width = 80,  minWidth = 50,  autoResize = false, canSort = true },
                };
                if (LogTimestamps.HasTimestamps)
                    columns.Add(new MultiColumnHeaderState.Column { headerContent = new GUIContent("Timestamp", $"Log timestamp at the start line ({LogTimestamps.DetectedFormatName}); hover a range row for start → end"), width = 160, minWidth = 70, autoResize = false, canSort = true, allowToggleVisibility = true });

                var state = new MultiColumnHeaderState(columns.ToArray());
                state.sortedColumnIndex          = 0;
                state.columns[0].sortedAscending = true;
                return state;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<SummaryRow> m_Entries         = new List<SummaryRow>();
        readonly List<SummaryRow> m_FilteredEntries = new List<SummaryRow>();
        string             m_StatusMsg = string.Empty;
        string             m_Filter    = string.Empty;
        SummaryTreeView    m_TreeView;
        TreeViewState<int> m_TreeState;
        EditorWindow       m_Window;

        BuildLogAnalyzerTab[] m_SourceTabs    = Array.Empty<BuildLogAnalyzerTab>();
        int[]                 m_SourceIndices = Array.Empty<int>();
        Action<int>           m_NavigateToTab;

        // ── BuildLogAnalyzerTab ───────────────────────────────────────────────

        public override string TabName => "Timeline";

        public override void OnEnable(EditorWindow window) => m_Window = window;

        /// <summary>Wires this tab to the tabs it should aggregate and how to switch to them by index in the window's tab array.</summary>
        public void SetSourceTabs(BuildLogAnalyzerTab[] sourceTabs, int[] sourceTabIndices, Action<int> navigateToTab)
        {
            m_SourceTabs    = sourceTabs;
            m_SourceIndices = sourceTabIndices;
            m_NavigateToTab = navigateToTab;
        }

        public override void Clear()
        {
            m_Entries.Clear();
            m_FilteredEntries.Clear();
            m_StatusMsg = string.Empty;
            m_TreeView  = null; // rebuild columns next parse (timestamp column may appear/disappear)
        }

        public override string GetStatusMessage() => m_StatusMsg;

        // Called last (see BuildLogAnalyzerWindow.ParseLog), once every other tab has parsed its entries.
        public override void ParseLines(string[] lines)
        {
            m_Entries.Clear();
            for (int i = 0; i < m_SourceTabs.Length; i++)
            {
                var tab = m_SourceTabs[i];
                foreach (var row in tab.GetSummaryRows())
                {
                    row.Category = tab.TabName;
                    row.TabIndex = m_SourceIndices[i];
                    m_Entries.Add(row);
                }
            }
            ApplyFilter();
        }

        public override void DrawGUI(float contentWidth)
        {
            if (m_Entries.Count == 0)
            {
                GUILayout.Label("No entries to summarize.", EditorStyles.centeredGreyMiniLabel);
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

        void NavigateToRow(SummaryRow row)
        {
            row.SourceTab?.SelectSummaryRow(row.LineNumber);
            m_NavigateToTab?.Invoke(row.TabIndex);
        }

        void ApplyFilter()
        {
            m_FilteredEntries.Clear();
            if (string.IsNullOrEmpty(m_Filter))
                m_FilteredEntries.AddRange(m_Entries);
            else
                foreach (var e in m_Entries)
                    if (e.Name.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0
                     || e.Category.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        m_FilteredEntries.Add(e);

            EnsureTreeView();
            m_TreeView.SetSource(m_FilteredEntries);

            int shown = m_FilteredEntries.Count, total = m_Entries.Count;
            m_StatusMsg = shown == total
                ? $"Showing {total} entries"
                : $"Showing {shown} of {total} entries";
        }

        void EnsureTreeView()
        {
            if (m_TreeView != null) return;
            if (m_TreeState == null) m_TreeState = new TreeViewState<int>();
            m_TreeView = new SummaryTreeView(m_TreeState, new MultiColumnHeader(SummaryTreeView.CreateDefaultHeaderState()));
            m_TreeView.OnNameClicked = NavigateToRow;
        }
    }
}
