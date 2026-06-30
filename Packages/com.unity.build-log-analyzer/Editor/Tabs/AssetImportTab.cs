using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BuildLogAnalyzer.Editor
{
    sealed class AssetImportTab : BuildLogAnalyzerTab
    {
        // ── Data model ────────────────────────────────────────────────────────

        sealed class AssetImportEntry
        {
            public int          LineNumber;
            public string       AssetPath;
            public string       AssetName;
            public float        TotalTimeSec;
            public int          ImportCount;
            public string       AssetGuid;
            public List<string> RefreshGuids = new List<string>();
        }

        sealed class ImportTreeView : TreeView<int>
        {
            List<AssetImportEntry> m_Source = new List<AssetImportEntry>();

            public ImportTreeView(TreeViewState<int> tvState, MultiColumnHeader header) : base(tvState, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<AssetImportEntry> entries)
            {
                m_Source = entries;
                SortSource();
                Reload();
            }

            // Returns the entry at the given sorted-list index (used for selection polling).
            public AssetImportEntry GetEntry(int sortedIndex)
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
                        1 => string.Compare(a.AssetName, b.AssetName, StringComparison.OrdinalIgnoreCase),
                        2 => a.TotalTimeSec.CompareTo(b.TotalTimeSec),
                        3 => a.ImportCount.CompareTo(b.ImportCount),
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
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].AssetName));
                SetupParentsAndChildrenFromDepths(root, items);
                return root;
            }

            protected override void SingleClickedItem(int id)
            {
                if (id < 0 || id >= m_Source.Count) return;
                var e = m_Source[id];

                if (!string.IsNullOrEmpty(e.AssetGuid))
                {
                    string guidPath = AssetDatabase.GUIDToAssetPath(e.AssetGuid);
                    if (!string.IsNullOrEmpty(guidPath))
                    {
                        var guidAsset = AssetDatabase.LoadMainAssetAtPath(guidPath);
                        if (guidAsset != null) { PingAndSelect(guidAsset); return; }
                    }
                }

                var pathAsset = AssetDatabase.LoadMainAssetAtPath(e.AssetPath.Replace('\\', '/'));
                if (pathAsset != null) PingAndSelect(pathAsset);
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
                    var content = col == 1
                        ? new GUIContent(e.AssetName, e.AssetPath)
                        : new GUIContent(col switch
                        {
                            0 => e.LineNumber.ToString(),
                            2 => e.TotalTimeSec.ToString("F4"),
                            3 => e.ImportCount.ToString(),
                            _ => string.Empty
                        });
                    EditorGUI.LabelField(rect, content);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",     "Log file line number of the first import"),                                      width = 55,  minWidth = 40, autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Asset"),                                                                                     width = 300, minWidth = 80, autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Time (s)", "Total import time in seconds (summed across all imports of this asset)"),        width = 80,  minWidth = 50, autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Count",    "Number of times this asset was imported"),                                       width = 60,  minWidth = 40, autoResize = false, canSort = true },
                });
                state.sortedColumnIndex          = 2;
                state.columns[2].sortedAscending = false;
                return state;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<AssetImportEntry> m_Entries         = new List<AssetImportEntry>();
        readonly List<AssetImportEntry> m_FilteredEntries = new List<AssetImportEntry>();
        string              m_StatusMsg       = string.Empty;
        string              m_ParseExtra      = string.Empty;
        string              m_Filter          = string.Empty;
        ImportTreeView      m_TreeView;
        TreeViewState<int>  m_TreeState;
        AssetImportEntry    m_Selected;
        Vector2             m_DetailScroll;
        float               m_DetailPanelHeight = 120f;
        bool                m_Resizing;
        EditorWindow        m_Window;

        AssetDatabaseRefreshTab m_RefreshTab;
        Action                  m_NavigateToRefreshTab;

        // ── BuildLogAnalyzerTab ───────────────────────────────────────────────

        public override string TabName => "Asset Importing";

        public override void OnEnable(EditorWindow window) => m_Window = window;

        public void SetRefreshTabNavigation(AssetDatabaseRefreshTab refreshTab, Action navigateAction)
        {
            m_RefreshTab           = refreshTab;
            m_NavigateToRefreshTab = navigateAction;
        }

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
            var dict    = new Dictionary<string, AssetImportEntry>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string path, string guid, int lineNumber)>();
            var pendingRefreshBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            const string k_StartImporting = "Start importing ";
            const string k_Using          = " using ";
            const string k_ArtifactId     = "(artifact id:";
            const string k_CloseParen     = ") in ";
            const string k_Seconds        = " seconds";
            const string k_RefreshMarker  = "Asset Pipeline Refresh (id=";

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string raw = lines[lineIdx];

                // When the refresh summary appears, retroactively assign its GUID to all
                // imports that completed since the previous refresh.
                int refreshIdx = raw.IndexOf(k_RefreshMarker, StringComparison.Ordinal);
                if (refreshIdx >= 0)
                {
                    int guidStart = refreshIdx + k_RefreshMarker.Length;
                    int guidEnd   = raw.IndexOf(')', guidStart);
                    if (guidEnd > guidStart)
                    {
                        string refreshGuid = raw.Substring(guidStart, guidEnd - guidStart);
                        foreach (string batchPath in pendingRefreshBatch)
                        {
                            if (!dict.TryGetValue(batchPath, out var batchEntry)) continue;
                            if (!batchEntry.RefreshGuids.Contains(refreshGuid))
                                batchEntry.RefreshGuids.Add(refreshGuid);
                        }
                        pendingRefreshBatch.Clear();
                    }
                    continue;
                }

                // "Start importing <path> using ..." — enqueue.
                int startIdx = raw.IndexOf(k_StartImporting, StringComparison.Ordinal);
                if (startIdx >= 0)
                {
                    int pathStart = startIdx + k_StartImporting.Length;
                    int usingIdx  = raw.IndexOf(k_Using, pathStart, StringComparison.Ordinal);
                    if (usingIdx > pathStart)
                    {
                        string assetPath = raw.Substring(pathStart, usingIdx - pathStart).Trim();
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            string guid     = string.Empty;
                            int    guidOpen = raw.IndexOf("Guid(", usingIdx + k_Using.Length, StringComparison.Ordinal);
                            if (guidOpen >= 0)
                            {
                                int gs = guidOpen + 5;
                                int ge = raw.IndexOf(')', gs);
                                if (ge > gs) guid = raw.Substring(gs, ge - gs);
                            }
                            pending.Enqueue((assetPath, guid, lineIdx + 1));
                        }
                    }
                }

                // "(artifact id: 'xxx') in Y seconds" — close the oldest pending import.
                int artifactIdx = raw.IndexOf(k_ArtifactId, StringComparison.Ordinal);
                if (artifactIdx >= 0 && pending.Count > 0)
                {
                    int closeIdx = raw.IndexOf(k_CloseParen, artifactIdx, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        int secStart = closeIdx + k_CloseParen.Length;
                        int secEnd   = raw.IndexOf(k_Seconds, secStart, StringComparison.Ordinal);
                        if (secEnd > secStart
                            && float.TryParse(
                                raw.Substring(secStart, secEnd - secStart),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out float timeSec))
                        {
                            var (assetPath, assetGuid, lineNumber) = pending.Dequeue();

                            if (!dict.TryGetValue(assetPath, out var entry))
                            {
                                entry = new AssetImportEntry
                                {
                                    LineNumber = lineNumber,
                                    AssetPath  = assetPath,
                                    AssetName  = Path.GetFileName(assetPath),
                                    AssetGuid  = assetGuid,
                                };
                                dict[assetPath] = entry;
                            }

                            entry.TotalTimeSec += timeSec;
                            entry.ImportCount++;
                            pendingRefreshBatch.Add(assetPath);
                        }
                    }
                }
            }

            m_Entries.AddRange(dict.Values);
            float totalTime = 0f;
            foreach (var e in m_Entries) totalTime += e.TotalTimeSec;
            m_ParseExtra = $"  |  Total Import Time: {FormatDuration(totalTime)}";
            ApplyFilter();
        }

        public override void DrawGUI(float contentWidth)
        {
            if (m_Entries.Count == 0)
            {
                GUILayout.Label("No asset import entries found.", EditorStyles.centeredGreyMiniLabel);
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
                GUI.Label(rect, "Select an asset to see its Asset DB Refresh associations.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var inner = new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8);

            float lh          = EditorGUIUtility.singleLineHeight + 2f;
            int   lineCount   = 2 + Mathf.Max(1, m_Selected.RefreshGuids.Count);
            float contentH    = lineCount * lh + 8f;
            var   contentRect = new Rect(0, 0, inner.width - 16f, Mathf.Max(contentH, inner.height));

            m_DetailScroll = GUI.BeginScrollView(inner, m_DetailScroll, contentRect);

            float y = 0f;
            GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                $"{m_Selected.AssetName}  —  imported {m_Selected.ImportCount}× in {m_Selected.TotalTimeSec:F4}s total",
                EditorStyles.boldLabel);
            y += lh + 4f;

            if (m_Selected.RefreshGuids.Count == 0)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "No Asset DB Refresh associations found.", EditorStyles.miniLabel);
            }
            else
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "Triggered by Asset DB Refreshes (click to jump):", EditorStyles.miniLabel);
                y += lh;
                foreach (string guid in m_Selected.RefreshGuids)
                {
                    if (GUI.Button(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight), guid, EditorStyles.linkLabel))
                        NavigateToRefresh(guid);
                    y += lh;
                }
            }

            GUI.EndScrollView();
        }

        void NavigateToRefresh(string guid)
        {
            m_RefreshTab?.SelectRefreshByGuid(guid);
            m_NavigateToRefreshTab?.Invoke();
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
                    if (e.AssetName.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0
                     || e.AssetPath.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        m_FilteredEntries.Add(e);

            EnsureTreeView();
            m_TreeView.SetSource(m_FilteredEntries);

            int shown = m_FilteredEntries.Count, total = m_Entries.Count;
            m_StatusMsg = shown == total
                ? $"Showing {total} assets{m_ParseExtra}"
                : $"Showing {shown} of {total} assets{m_ParseExtra}";
        }

        void EnsureTreeView()
        {
            if (m_TreeView != null) return;
            if (m_TreeState == null) m_TreeState = new TreeViewState<int>();
            m_TreeView = new ImportTreeView(m_TreeState, new MultiColumnHeader(ImportTreeView.CreateDefaultHeaderState()));
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
