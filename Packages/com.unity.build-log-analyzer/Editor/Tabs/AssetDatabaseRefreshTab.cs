using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BuildLogAnalyzer.Editor
{
    sealed class AssetDatabaseRefreshTab : BuildLogAnalyzerTab
    {
        // ── Data model ────────────────────────────────────────────────────────

        sealed class RefreshEntry
        {
            public int          LineNumber;
            public int          LineNumberEnd;
            public string       Guid;
            public float        TotalTimeSec;
            public string       Reason;
            public List<string> DetailLines = new List<string>();
        }

        sealed class RefreshTreeView : TreeView<int>
        {
            List<RefreshEntry> m_Source = new List<RefreshEntry>();

            public RefreshTreeView(TreeViewState<int> state, MultiColumnHeader header) : base(state, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<RefreshEntry> entries)
            {
                m_Source = entries;
                SortSource();
                Reload();
            }

            // Returns the entry at the given sorted-list index (used for selection polling).
            public RefreshEntry GetEntry(int sortedIndex)
                => (sortedIndex >= 0 && sortedIndex < m_Source.Count) ? m_Source[sortedIndex] : null;

            public void SelectByGuid(string guid)
            {
                for (int i = 0; i < m_Source.Count; i++)
                {
                    if (m_Source[i].Guid != guid) continue;
                    SetSelection(new List<int> { i });
                    FrameItem(i);
                    return;
                }
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
                        1 => string.Compare(a.Guid, b.Guid, StringComparison.Ordinal),
                        2 => a.TotalTimeSec.CompareTo(b.TotalTimeSec),
                        3 => string.Compare(a.Reason, b.Reason, StringComparison.OrdinalIgnoreCase),
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
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].Guid));
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
                        0 => e.LineNumberEnd > e.LineNumber ? $"{e.LineNumber}–{e.LineNumberEnd}" : e.LineNumber.ToString(),
                        1 => e.Guid,
                        2 => e.TotalTimeSec.ToString("F3"),
                        3 => e.Reason,
                        _ => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",       "Log file line range of this refresh block (start–end)"),  width = 90,  minWidth = 55, autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Refresh ID", "Asset Pipeline Refresh GUID"),     width = 280, minWidth = 80, autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Time (s)",   "Total refresh duration"),          width = 75,  minWidth = 50, autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Reason",     "What initiated the refresh"),      width = 220, minWidth = 80, autoResize = false, canSort = true },
                });
                state.sortedColumnIndex          = 0;
                state.columns[0].sortedAscending = true;
                return state;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<RefreshEntry> m_Entries         = new List<RefreshEntry>();
        readonly List<RefreshEntry> m_FilteredEntries = new List<RefreshEntry>();
        string             m_StatusMsg         = string.Empty;
        string             m_Filter            = string.Empty;
        RefreshTreeView    m_TreeView;
        TreeViewState<int> m_TreeState;
        RefreshEntry       m_Selected;
        Vector2            m_DetailScroll;
        float              m_DetailPanelHeight = 120f;
        bool               m_Resizing;
        EditorWindow       m_Window;

        AssetImportTab     m_ImportTab;
        Action             m_NavigateToImportTab;

        // ── BuildLogAnalyzerTab ───────────────────────────────────────────────

        public override string TabName => "Asset DB Refreshes";

        public override void OnEnable(EditorWindow window) => m_Window = window;

        public override void Clear()
        {
            m_Entries.Clear();
            m_FilteredEntries.Clear();
            m_StatusMsg = string.Empty;
            m_Selected  = null;
        }

        public override string GetStatusMessage() => m_StatusMsg;

        public void SetImportTabNavigation(AssetImportTab importTab, Action navigateAction)
        {
            m_ImportTab           = importTab;
            m_NavigateToImportTab = navigateAction;
        }

        public void SelectRefreshByGuid(string guid)
        {
            // Clear filter if the target would be hidden.
            bool inFilter = false;
            foreach (var e in m_FilteredEntries) { if (e.Guid == guid) { inFilter = true; break; } }
            if (!inFilter) { m_Filter = string.Empty; ApplyFilter(); }

            EnsureTreeView();
            m_TreeView.SelectByGuid(guid);

            foreach (var e in m_Entries) { if (e.Guid == guid) { m_Selected = e; break; } }
            m_Window?.Repaint();
        }

        public override void ParseLines(string[] lines)
        {
            const string k_RefreshMarker   = "Asset Pipeline Refresh (id=";
            const string k_TotalMarker     = "Total: ";
            const string k_SecondsMarker   = " seconds";
            const string k_InitiatedMarker = "- Initiated by ";
            const int    k_MaxDetailLines  = 200;

            for (int i = 0; i < lines.Length; i++)
            {
                string raw        = lines[i];
                int    refreshIdx = raw.IndexOf(k_RefreshMarker, StringComparison.Ordinal);
                if (refreshIdx < 0) continue;

                int guidStart = refreshIdx + k_RefreshMarker.Length;
                int guidEnd   = raw.IndexOf(')', guidStart);
                if (guidEnd <= guidStart) continue;
                string guid = raw.Substring(guidStart, guidEnd - guidStart);

                float timeSec  = 0f;
                int   totalIdx = raw.IndexOf(k_TotalMarker, guidEnd, StringComparison.Ordinal);
                if (totalIdx >= 0)
                {
                    int ts = totalIdx + k_TotalMarker.Length;
                    int te = raw.IndexOf(k_SecondsMarker, ts, StringComparison.Ordinal);
                    if (te > ts)
                        float.TryParse(raw.Substring(ts, te - ts),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out timeSec);
                }

                string reason      = string.Empty;
                int    initiatedIdx = raw.IndexOf(k_InitiatedMarker, StringComparison.Ordinal);
                if (initiatedIdx >= 0)
                    reason = raw.Substring(initiatedIdx + k_InitiatedMarker.Length).Trim();

                // Collect the breakdown lines that follow this header.
                // Stop at the next refresh entry, a blank line, or after k_MaxDetailLines.
                var details = new List<string>();
                for (int j = i + 1; j < lines.Length && details.Count < k_MaxDetailLines; j++)
                {
                    if (lines[j].IndexOf(k_RefreshMarker, StringComparison.Ordinal) >= 0) break;
                    string trimmed = lines[j].TrimStart();
                    if (trimmed.Length == 0) break;
                    details.Add(trimmed);
                }

                m_Entries.Add(new RefreshEntry
                {
                    LineNumber    = i + 1,
                    LineNumberEnd = i + 1 + details.Count,
                    Guid          = guid,
                    TotalTimeSec  = timeSec,
                    Reason        = reason,
                    DetailLines   = details,
                });
            }

            ApplyFilter();
        }

        public override void DrawGUI(float contentWidth)
        {
            if (m_Entries.Count == 0)
            {
                GUILayout.Label("No Asset Database refresh entries found.", EditorStyles.centeredGreyMiniLabel);
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

            // Tree fills remaining space; resizer and details have fixed heights.
            Rect treeRect    = GUILayoutUtility.GetRect(contentWidth, 50f, GUILayout.ExpandHeight(true));
            Rect resizerRect = GUILayoutUtility.GetRect(contentWidth, 5f,  GUILayout.Height(5f));
            Rect detailRect  = GUILayoutUtility.GetRect(contentWidth, m_DetailPanelHeight, GUILayout.Height(m_DetailPanelHeight));

            m_TreeView.OnGUI(treeRect);

            // Poll selection from tree — avoids dependence on event callbacks.
            var sel = m_TreeView.GetSelection();
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
                GUI.Label(rect, "Select a refresh to see details.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var imports = m_ImportTab?.GetImportsForRefreshGuid(m_Selected.Guid)
                          ?? new List<(string name, string path, float totalTime)>();

            var   inner = new Rect(rect.x + 4, rect.y + 4, rect.width - 8, rect.height - 8);
            float lh    = EditorGUIUtility.singleLineHeight + 2f;

            // Section heights: header + asset rows + gap + detail lines.
            int   assetRows   = Mathf.Max(1, imports.Count);
            float contentH    = lh + assetRows * lh + lh + m_Selected.DetailLines.Count * lh;
            var   contentRect = new Rect(0, 0, inner.width - 16f, Mathf.Max(contentH, inner.height));

            m_DetailScroll = GUI.BeginScrollView(inner, m_DetailScroll, contentRect);
            float y = 0f;

            // ── Assets section ────────────────────────────────────────────────
            GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                imports.Count == 0 ? "Imported assets: none" : $"Imported assets ({imports.Count}):",
                EditorStyles.boldLabel);
            y += lh;

            if (imports.Count == 0)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "  No asset imports associated with this refresh.", EditorStyles.miniLabel);
                y += lh;
            }
            else
            {
                foreach (var (name, path, totalTime) in imports)
                {
                    string label = $"  {name}  ({totalTime:F4}s)";
                    if (GUI.Button(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                            new GUIContent(label, path), EditorStyles.linkLabel))
                        NavigateToImport(path);
                    y += lh;
                }
            }

            y += 4f;

            // ── Refresh detail lines ──────────────────────────────────────────
            foreach (string line in m_Selected.DetailLines)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    line, EditorStyles.miniLabel);
                y += lh;
            }

            GUI.EndScrollView();
        }

        void NavigateToImport(string path)
        {
            m_ImportTab?.SelectImportByPath(path);
            m_NavigateToImportTab?.Invoke();
        }

        void HandleSplitterDrag(Rect resizerRect)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && resizerRect.Contains(e.mousePosition))
            { m_Resizing = true; e.Use(); return; }
            if (m_Resizing && e.type == EventType.MouseDrag)
            {
                // Dragging down (delta.y > 0) shrinks the details panel; up grows it.
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
                    if (e.Guid.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0
                     || e.Reason.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        m_FilteredEntries.Add(e);

            EnsureTreeView();
            m_TreeView.SetSource(m_FilteredEntries);

            float totalTime = 0f;
            foreach (var e in m_Entries) totalTime += e.TotalTimeSec;
            string timeExtra = $"  |  Total Refresh Time: {FormatDuration(totalTime)}";

            int shown = m_FilteredEntries.Count, total = m_Entries.Count;
            m_StatusMsg = shown == total
                ? $"Showing {total} refreshes{timeExtra}"
                : $"Showing {shown} of {total} refreshes{timeExtra}";
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

        void EnsureTreeView()
        {
            if (m_TreeView != null) return;
            if (m_TreeState == null) m_TreeState = new TreeViewState<int>();
            m_TreeView = new RefreshTreeView(m_TreeState, new MultiColumnHeader(RefreshTreeView.CreateDefaultHeaderState()));
        }
    }
}
