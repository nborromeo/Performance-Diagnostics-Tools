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
            public int          ImportedAssetCount;

            // Populated by FlagDuplicateAssetSetRefreshes() after parsing
            public List<RowWarning>   Warnings;
            public List<RefreshEntry> DuplicateAssetSetGroup; // other refreshes that touched the exact same assets
        }

        // ── Warning analyzers ─────────────────────────────────────────────────

        // Detects refreshes that touched the exact same set of assets as one or more
        // other refreshes — a common symptom of redundant AssetDatabase.Refresh() calls.
        static void FlagDuplicateAssetSetRefreshes(List<RefreshEntry> entries, AssetImportTab importTab)
        {
            if (importTab == null) return;

            var groups = new Dictionary<string, List<RefreshEntry>>(StringComparer.Ordinal);
            var pathsByEntry = new Dictionary<RefreshEntry, List<string>>();

            foreach (var e in entries)
            {
                var paths = new List<string>();
                foreach (var (_, path, _) in importTab.GetImportsForRefreshGuid(e.Guid))
                    paths.Add(path);
                e.ImportedAssetCount = paths.Count;
                if (paths.Count == 0) continue;

                paths.Sort(StringComparer.OrdinalIgnoreCase);
                pathsByEntry[e] = paths;

                string key = string.Join("\n", paths);
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = new List<RefreshEntry>();
                group.Add(e);
            }

            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;

                foreach (var e in group)
                {
                    var others = new List<RefreshEntry>();
                    foreach (var other in group)
                        if (other != e) others.Add(other);

                    int assetCount = pathsByEntry[e].Count;
                    e.DuplicateAssetSetGroup = others;
                    e.Warnings ??= new List<RowWarning>();
                    e.Warnings.Add(new RowWarning(
                        $"This refresh touched the exact same set of {assetCount} asset(s) as {others.Count} other refresh(es) (see links below). " +
                        "Repeated refreshes over an identical asset set often indicate a redundant or overly broad AssetDatabase.Refresh() call."));
                }
            }
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

            public void SelectByLine(int line)
            {
                for (int i = 0; i < m_Source.Count; i++)
                {
                    if (m_Source[i].LineNumber != line) continue;
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
                        1 => (a.Warnings?.Count ?? 0).CompareTo(b.Warnings?.Count ?? 0),
                        2 => string.Compare(a.Guid, b.Guid, StringComparison.Ordinal),
                        3 => a.TotalTimeSec.CompareTo(b.TotalTimeSec),
                        4 => string.Compare(a.Reason, b.Reason, StringComparison.OrdinalIgnoreCase),
                        5 => a.ImportedAssetCount.CompareTo(b.ImportedAssetCount),
                        6 => a.LineNumber.CompareTo(b.LineNumber), // chronological == line order; survives midnight rollover
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
                    int col = args.GetColumn(i);

                    if (col == 0)
                    {
                        LogFileNavigator.DrawLineCell(rect, e.LineNumber, e.LineNumberEnd);
                        continue;
                    }

                    if (col == 6)
                    {
                        LogFileNavigator.DrawTimestampCell(rect, e.LineNumber, e.LineNumberEnd);
                        continue;
                    }

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
                        2 => e.Guid,
                        3 => e.TotalTimeSec.ToString("F3"),
                        4 => e.Reason,
                        5 => e.ImportedAssetCount.ToString(),
                        _ => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var columns = new List<MultiColumnHeaderState.Column>
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",       "Line range of this refresh: start = first import since the previous refresh, end = the refresh summary line"),  width = 90,  minWidth = 55, autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("⚠",          "Number of warnings detected for this entry"),                                                                   width = 30,  minWidth = 25, autoResize = false, canSort = true, allowToggleVisibility = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Refresh ID", "Asset Pipeline Refresh GUID"),     width = 280, minWidth = 80, autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Time (s)",   "Total refresh duration"),          width = 75,  minWidth = 50, autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Reason",     "What initiated the refresh"),      width = 220, minWidth = 80, autoResize = false, canSort = true },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Assets",     "Number of assets imported as part of this refresh"), width = 60, minWidth = 45, autoResize = false, canSort = true },
                };
                if (LogTimestamps.HasTimestamps)
                    columns.Add(new MultiColumnHeaderState.Column { headerContent = new GUIContent("Timestamp", $"Log timestamp at the start line ({LogTimestamps.DetectedFormatName}); hover a range row for start → end"), width = 160, minWidth = 70, autoResize = false, canSort = true, allowToggleVisibility = true });

                var state = new MultiColumnHeaderState(columns.ToArray());
                state.sortedColumnIndex          = 0;
                state.columns[0].sortedAscending = true;
                return state;
            }
        }

        // ── Imported-assets sub-table ─────────────────────────────────────────

        // Small virtualized table shown in the details panel, replacing a flat loop of
        // GUI.Button rows — cheap even when a refresh touched thousands of assets.
        sealed class ImportedAssetsTreeView : TreeView<int>
        {
            List<(string name, string path, float totalTime)> m_Source = new List<(string, string, float)>();
            readonly Action<string> m_OnSelectPath;

            public ImportedAssetsTreeView(TreeViewState<int> state, MultiColumnHeader header, Action<string> onSelectPath) : base(state, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                m_OnSelectPath                = onSelectPath;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<(string name, string path, float totalTime)> entries)
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
                        0 => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase),
                        1 => a.totalTime.CompareTo(b.totalTime),
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
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].name));
                SetupParentsAndChildrenFromDepths(root, items);
                return root;
            }

            protected override void SingleClickedItem(int id)
            {
                if (id < 0 || id >= m_Source.Count) return;
                m_OnSelectPath?.Invoke(m_Source[id].path);
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
                        EditorGUI.LabelField(rect, new GUIContent(e.name, e.path));
                        continue;
                    }

                    EditorGUI.LabelField(rect, e.totalTime.ToString("F4"));
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var columns = new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Asset"),    width = 260, minWidth = 100, autoResize = true,  canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Time (s)"), width = 70,  minWidth = 50,  autoResize = false, canSort = true },
                };
                var state = new MultiColumnHeaderState(columns);
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

        ImportedAssetsTreeView m_ImportsTreeView;
        TreeViewState<int>     m_ImportsTreeState;
        List<(string name, string path, float totalTime)> m_SelectedImports = new List<(string, string, float)>();

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
            m_TreeView  = null; // rebuild columns next parse (timestamp column may appear/disappear)
            m_SelectedImports.Clear();
            m_ImportsTreeView?.SetSource(m_SelectedImports);
        }

        public override string GetStatusMessage() => m_StatusMsg;

        public override IEnumerable<SummaryRow> GetSummaryRows()
        {
            foreach (var e in m_Entries)
                yield return new SummaryRow
                {
                    LineNumber    = e.LineNumber,
                    LineNumberEnd = e.LineNumberEnd,
                    Name          = string.IsNullOrEmpty(e.Reason) ? $"Refresh {e.Guid}" : $"Refresh — {e.Reason}",
                    DurationSec   = e.TotalTimeSec,
                    SourceTab     = this,
                };
        }

        public override void SelectSummaryRow(int lineNumber)
        {
            RefreshEntry match = null;
            foreach (var e in m_Entries)
                if (e.LineNumber == lineNumber) { match = e; break; }
            if (match == null) return;

            if (!m_FilteredEntries.Contains(match)) { m_Filter = string.Empty; ApplyFilter(); }

            EnsureTreeView();
            m_TreeView.SelectByLine(lineNumber);
            m_Selected = match;
            RefreshSelectedImports();
            m_Window?.Repaint();
        }

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
            RefreshSelectedImports();
            m_Window?.Repaint();
        }

        public override void ParseLines(string[] lines)
        {
            const string k_RefreshMarker   = "Asset Pipeline Refresh (id=";
            const string k_TotalMarker     = "Total: ";
            const string k_SecondsMarker   = " seconds";
            const string k_InitiatedMarker = "- Initiated by ";
            const string k_StartImporting  = "Start importing ";
            const int    k_MaxDetailLines  = 200;

            // The refresh summary line is printed at the *end* of the refresh, after the imports
            // it triggered. So the block spans from the first import since the previous refresh
            // (start) to this summary line (end).
            int firstImportLine = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string raw        = lines[i];
                int    refreshIdx = raw.IndexOf(k_RefreshMarker, StringComparison.Ordinal);
                if (refreshIdx < 0)
                {
                    if (firstImportLine < 0 && raw.IndexOf(k_StartImporting, StringComparison.Ordinal) >= 0)
                        firstImportLine = i + 1;
                    continue;
                }

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
                    LineNumber    = firstImportLine > 0 ? firstImportLine : i + 1,
                    LineNumberEnd = i + 1, // the "Asset Pipeline Refresh (id=…)" summary line
                    Guid          = guid,
                    TotalTimeSec  = timeSec,
                    Reason        = reason,
                    DetailLines   = details,
                });

                firstImportLine = -1; // begin a fresh window for the next refresh
            }

            FlagDuplicateAssetSetRefreshes(m_Entries, m_ImportTab);
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
            if (newSelected != m_Selected) { m_Selected = newSelected; m_DetailScroll = Vector2.zero; RefreshSelectedImports(); }

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

            var   inner    = new Rect(rect.x + 4, rect.y + 4, rect.width - 8, rect.height - 8);
            float lh       = EditorGUIUtility.singleLineHeight + 2f;
            var   warnings = m_Selected.Warnings;
            int   wc       = warnings?.Count ?? 0;

            // The imported-assets table is a virtualized TreeView (cheap regardless of row
            // count), so it gets a fixed reserved height rather than a per-row height sum.
            float assetsHeight = m_SelectedImports.Count == 0 ? lh : Mathf.Clamp(m_SelectedImports.Count * 18f + 22f, 60f, 260f);

            // Section heights: warnings + header + assets area + gap + detail lines.
            var   wrapStyle    = EditorStyles.wordWrappedMiniLabel;
            float wrapWidth    = Mathf.Max(inner.width - 32f, 100f);
            float warningsH    = lh; // "N Warning(s)" / "No warnings" label
            if (wc > 0)
            {
                foreach (var w in warnings)
                    warningsH += wrapStyle.CalcHeight(new GUIContent($"• {w.Message}"), wrapWidth) + 2f;
                warningsH += (m_Selected.DuplicateAssetSetGroup?.Count ?? 0) * lh;
            }
            warningsH += 4f;

            float contentH    = warningsH + lh + assetsHeight + 4f + m_Selected.DetailLines.Count * lh;
            var   contentRect = new Rect(0, 0, inner.width - 16f, Mathf.Max(contentH, inner.height));

            m_DetailScroll = GUI.BeginScrollView(inner, m_DetailScroll, contentRect);
            float y = 0f;

            // ── Warnings section ──────────────────────────────────────────────
            if (wc == 0)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "No warnings detected.", EditorStyles.miniLabel);
                y += lh;
            }
            else
            {
                var warnIcon = EditorGUIUtility.IconContent("console.warnicon.inactive.sml");
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    new GUIContent($" {wc} Warning{(wc > 1 ? "s" : "")}", warnIcon.image),
                    EditorStyles.boldLabel);
                y += lh;

                foreach (var w in warnings)
                {
                    string msg  = $"• {w.Message}";
                    float  msgH = wrapStyle.CalcHeight(new GUIContent(msg), wrapWidth);
                    GUI.Label(new Rect(8, y, wrapWidth, msgH), msg, wrapStyle);
                    y += msgH + 2f;
                }

                if (m_Selected.DuplicateAssetSetGroup != null)
                {
                    foreach (var other in m_Selected.DuplicateAssetSetGroup)
                    {
                        string label = $"    → line {other.LineNumber}  (id={other.Guid})";
                        if (GUI.Button(new Rect(8, y, wrapWidth, EditorGUIUtility.singleLineHeight), label, EditorStyles.linkLabel))
                            SelectDuplicateRefresh(other);
                        y += lh;
                    }
                }
            }
            y += 4f;

            // ── Assets section ────────────────────────────────────────────────
            GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                m_SelectedImports.Count == 0 ? "Imported assets: none" : $"Imported assets ({m_SelectedImports.Count}):",
                EditorStyles.boldLabel);
            y += lh;

            if (m_SelectedImports.Count == 0)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    "  No asset imports associated with this refresh.", EditorStyles.miniLabel);
            }
            else
            {
                EnsureImportsTreeView();
                m_ImportsTreeView.OnGUI(new Rect(0, y, contentRect.width, assetsHeight));
            }
            y += assetsHeight + 4f;

            // ── Refresh detail lines ──────────────────────────────────────────
            foreach (string line in m_Selected.DetailLines)
            {
                GUI.Label(new Rect(0, y, contentRect.width, EditorGUIUtility.singleLineHeight),
                    line, EditorStyles.miniLabel);
                y += lh;
            }

            GUI.EndScrollView();
        }

        void RefreshSelectedImports()
        {
            m_SelectedImports = m_Selected != null
                ? (m_ImportTab?.GetImportsForRefreshGuid(m_Selected.Guid) ?? new List<(string, string, float)>())
                : new List<(string, string, float)>();
            EnsureImportsTreeView();
            m_ImportsTreeView.SetSource(m_SelectedImports);
        }

        void EnsureImportsTreeView()
        {
            if (m_ImportsTreeView != null) return;
            if (m_ImportsTreeState == null) m_ImportsTreeState = new TreeViewState<int>();
            m_ImportsTreeView = new ImportedAssetsTreeView(m_ImportsTreeState, new MultiColumnHeader(ImportedAssetsTreeView.CreateDefaultHeaderState()), NavigateToImport);
        }

        void NavigateToImport(string path)
        {
            m_ImportTab?.SelectImportByPath(path);
            m_NavigateToImportTab?.Invoke();
        }

        // Jumps to another refresh entry within this same tab (used by duplicate-asset-set warning links).
        void SelectDuplicateRefresh(RefreshEntry target)
        {
            if (!m_FilteredEntries.Contains(target)) { m_Filter = string.Empty; ApplyFilter(); }

            EnsureTreeView();
            m_TreeView.SelectByGuid(target.Guid);
            m_Selected     = target;
            m_DetailScroll = Vector2.zero;
            RefreshSelectedImports();
            m_Window?.Repaint();
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
