using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ImportActivityViewer.Editor
{
    /// <summary>
    /// Companion to Unity's built-in Import Activity window. Instead of a flat list of every
    /// asset that was reimported, this groups them by cascade: each row on the left is a root
    /// asset that was imported for its own reason (edited, VCS update, etc.), and selecting it
    /// shows the full chain of dependents it dragged along on the right.
    /// </summary>
    public sealed class ImportActivityViewerWindow : EditorWindow
    {
        const float k_RowHeight = 20f;
        const float k_ToolbarHeight = 21f;
        const float k_ListInitWidth = 420f;

        const string k_ColRoot = "root";
        const string k_ColAffected = "affected";
        const string k_ColDuration = "duration";
        const string k_ColLastImport = "last-import";
        const string k_ColReason = "reason";

        const string k_ColTreeAsset = "tree-asset";
        const string k_ColTreeDuration = "tree-duration";
        const string k_ColTreeImporter = "tree-importer";
        const string k_ColTreeReason = "tree-reason";

        MultiColumnListView m_RootListView;
        MultiColumnTreeView m_ChainTreeView;
        ToolbarSearchField m_RootSearchField;
        ToolbarSearchField m_TreeSearchField;
        string m_StatusText = "";
        bool m_StatusIsError;

        string m_RootSearchText = "";
        string m_TreeSearchText = "";
        CascadeGroup m_SelectedGroup;

        // Everything the last analysis produced. m_Groups is the filtered/sorted subset actually
        // bound to the list view.
        readonly List<CascadeGroup> m_AllGroups = new List<CascadeGroup>();
        readonly List<CascadeGroup> m_Groups = new List<CascadeGroup>();
        readonly Dictionary<string, Texture> m_IconCache = new Dictionary<string, Texture>();

        // Large logs (tens of thousands of entries) can take a long time to analyze because most
        // of the cost is native round-trips per asset. FetchAllCurrentRevisionsIncremental yields
        // periodically so this drives it like a coroutine instead of blocking the editor, and
        // re-runs the (much cheaper, O(n)) cascade analysis every so often so the list/tree
        // visibly fill in as data comes back rather than staying blank until everything is done.
        // Both knobs are tunable from the toolbar rather than hardcoded: a small time slice keeps
        // the editor maximally responsive but adds per-step overhead that can dominate on huge
        // logs, and a small analysis interval shows progress sooner but re-runs the O(n) cascade
        // analysis more often -- the right tradeoff depends on the size of the log being analyzed.
        const double k_DefaultTimeSliceMs = 8.0;
        const int k_DefaultAnalysisRefreshInterval = 3000;
        double m_TimeSliceMs = k_DefaultTimeSliceMs;
        int m_AnalysisRefreshInterval = k_DefaultAnalysisRefreshInterval;
        IEnumerator m_RefreshRoutine;
        readonly List<AssetImportRecord> m_LiveRecords = new List<AssetImportRecord>();
        int m_LastAnalyzedCount;
        bool m_IsRefreshing;

        Texture GetIcon(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;
            if (!m_IconCache.TryGetValue(assetPath, out Texture icon))
                m_IconCache[assetPath] = icon = AssetDatabase.GetCachedIcon(assetPath);
            return icon;
        }

        [MenuItem("Window/Analysis/Import Activity Viewer")]
        static void Open() => GetWindow<ImportActivityViewerWindow>("Import Activity Viewer");

        public void CreateGUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            var toolbar = new IMGUIContainer(DrawToolbar);
            toolbar.style.height = k_ToolbarHeight;
            toolbar.style.flexShrink = 0;
            rootVisualElement.Add(toolbar);

            var split = new TwoPaneSplitView(0, k_ListInitWidth, TwoPaneSplitViewOrientation.Horizontal)
            {
                style = { flexGrow = 1 },
            };

            BuildRootListView();
            var leftPane = new VisualElement { style = { flexDirection = FlexDirection.Column, minWidth = 200, flexGrow = 1 } };
            m_RootSearchField = new ToolbarSearchField { style = { flexShrink = 0 } };
            m_RootSearchField.RegisterValueChangedCallback(evt =>
            {
                m_RootSearchText = evt.newValue;
                ApplyRootFilter();
            });
            leftPane.Add(m_RootSearchField);
            m_RootListView.style.flexGrow = 1;
            leftPane.Add(m_RootListView);
            split.Add(leftPane);

            BuildChainTreeView();
            var rightPane = new VisualElement { style = { flexDirection = FlexDirection.Column, minWidth = 250, flexGrow = 1 } };
            m_TreeSearchField = new ToolbarSearchField { style = { flexShrink = 0 } };
            m_TreeSearchField.RegisterValueChangedCallback(evt =>
            {
                m_TreeSearchText = evt.newValue;
                RebuildTreeView();
            });
            rightPane.Add(m_TreeSearchField);
            m_ChainTreeView.style.flexGrow = 1;
            rightPane.Add(m_ChainTreeView);
            split.Add(rightPane);

            rootVisualElement.Add(split);

            Refresh();
        }

        // ── Data ─────────────────────────────────────────────────────────────
        void Refresh()
        {
            StopRefresh();

            m_IconCache.Clear();
            m_LiveRecords.Clear();
            m_AllGroups.Clear();
            m_SelectedGroup = null;
            m_LastAnalyzedCount = 0;
            ApplyRootFilter();
            RebuildTreeView();

            m_RefreshRoutine = ImportActivityReflection.FetchAllCurrentRevisionsIncremental(m_LiveRecords, () => m_TimeSliceMs);
            m_IsRefreshing = true;
            EditorApplication.update += StepRefresh;
            StepRefresh();
        }

        void StepRefresh()
        {
            bool more;
            try
            {
                more = m_RefreshRoutine.MoveNext();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                more = false;
            }

            bool shouldAnalyze = !more || (m_LiveRecords.Count - m_LastAnalyzedCount) >= m_AnalysisRefreshInterval;
            if (shouldAnalyze)
            {
                m_LastAnalyzedCount = m_LiveRecords.Count;
                m_AllGroups.Clear();
                m_AllGroups.AddRange(CascadeAnalyzer.Analyze(m_LiveRecords));
                ApplyRootFilter();
                RebuildTreeView();
            }

            UpdateStatusLabel(m_LiveRecords.Count, !more);
            Repaint();

            if (!more)
                StopRefresh();
        }

        void StopRefresh()
        {
            if (!m_IsRefreshing)
                return;
            EditorApplication.update -= StepRefresh;
            m_RefreshRoutine = null;
            m_IsRefreshing = false;
        }

        void OnDisable() => StopRefresh();

        void ApplyRootFilter()
        {
            m_Groups.Clear();
            if (string.IsNullOrEmpty(m_RootSearchText))
            {
                m_Groups.AddRange(m_AllGroups);
            }
            else
            {
                m_Groups.AddRange(m_AllGroups.Where(g =>
                    g.Root.DisplayName.IndexOf(m_RootSearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    g.Root.AssetPath.IndexOf(m_RootSearchText, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            m_RootListView.itemsSource = m_Groups;
            SortRoots();
            m_RootListView.RefreshItems();
        }

        void UpdateStatusLabel(int assetCount, bool done)
        {
            if (!string.IsNullOrEmpty(ImportActivityReflection.LastError))
            {
                m_StatusText = ImportActivityReflection.LastError;
                m_StatusIsError = true;
                return;
            }

            m_StatusIsError = false;
            int cascades = m_Groups.Count(g => g.AffectedCount > 0);

            if (done)
            {
                m_StatusText = $"{assetCount} asset revision{(assetCount == 1 ? "" : "s")} · {m_Groups.Count} root{(m_Groups.Count == 1 ? "" : "s")} · {cascades} cascade{(cascades == 1 ? "" : "s")}";
                return;
            }

            int total = ImportActivityReflection.LastTotalCount;
            string progress = total >= 0
                ? $"{ImportActivityReflection.LastProcessedCount}/{total}"
                : assetCount.ToString();
            m_StatusText = $"Analyzing... {progress} asset revisions · {m_Groups.Count} root{(m_Groups.Count == 1 ? "" : "s")} · {cascades} cascade{(cascades == 1 ? "" : "s")}";
        }

        // ── Toolbar ──────────────────────────────────────────────────────────
        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button(m_IsRefreshing ? "Cancel" : "Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                if (m_IsRefreshing)
                {
                    StopRefresh();
                    UpdateStatusLabel(m_LiveRecords.Count, true);
                }
                else
                {
                    Refresh();
                }
            }

            GUILayout.Space(6);

            GUIStyle style = m_StatusIsError ? EditorStyles.miniLabel : EditorStyles.miniLabel;
            Color prevColor = GUI.contentColor;
            if (m_StatusIsError)
                GUI.contentColor = new Color(1f, 0.55f, 0.45f);
            GUILayout.Label(m_StatusText, style);
            GUI.contentColor = prevColor;

            GUILayout.FlexibleSpace();

            // Both tunable live: the time slice takes effect on the very next step (it's read via
            // a delegate, not captured when the fetch coroutine was created), and the analysis
            // interval is read fresh by StepRefresh every step regardless.
            GUILayout.Label(new GUIContent("Time slice (ms)", "How long each background step is allowed to run before yielding back to the editor. Lower keeps the UI more responsive but adds overhead on huge logs; higher processes faster but can make the editor feel less responsive while refreshing."), EditorStyles.miniLabel);
            double newSlice = EditorGUILayout.DoubleField(m_TimeSliceMs, GUILayout.Width(40));
            m_TimeSliceMs = Math.Max(0.5, newSlice);

            GUILayout.Space(10);

            GUILayout.Label(new GUIContent("Refresh every", "How many new asset revisions to process before re-running cascade analysis and updating the list/tree. Lower shows progress sooner but re-analyzes more often; higher is faster overall but updates the UI less frequently."), EditorStyles.miniLabel);
            int newInterval = EditorGUILayout.IntField(m_AnalysisRefreshInterval, GUILayout.Width(60));
            m_AnalysisRefreshInterval = Math.Max(50, newInterval);

            EditorGUILayout.EndHorizontal();
        }

        // ── Root list (left) ─────────────────────────────────────────────────
        void BuildRootListView()
        {
            var columns = new Columns
            {
                new Column
                {
                    name = k_ColRoot,
                    title = "Root Asset",
                    width = 200,
                    minWidth = 100,
                    resizable = true,
                    sortable = true,
                    makeCell = () => MakeIconLabelCell(),
                    bindCell = BindRootCell,
                },
                new Column
                {
                    name = k_ColAffected,
                    title = "Assets Affected",
                    width = 110,
                    minWidth = 70,
                    resizable = true,
                    sortable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) => ((Label)el).text = i < m_Groups.Count ? m_Groups[i].AffectedCount.ToString() : "",
                },
                new Column
                {
                    name = k_ColDuration,
                    title = "Total Import Time",
                    width = 130,
                    minWidth = 80,
                    resizable = true,
                    sortable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) => ((Label)el).text = i < m_Groups.Count ? FormatMs(m_Groups[i].TotalImportMs) : "",
                },
                new Column
                {
                    name = k_ColLastImport,
                    title = "Last Import",
                    width = 150,
                    minWidth = 100,
                    resizable = true,
                    sortable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) => ((Label)el).text = i < m_Groups.Count ? m_Groups[i].Root.TimeStampDisplay : "",
                },
                new Column
                {
                    name = k_ColReason,
                    title = "Reason",
                    width = 220,
                    minWidth = 100,
                    resizable = true,
                    sortable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) => ((Label)el).text = i < m_Groups.Count ? m_Groups[i].Reason : "",
                },
            };

            m_RootListView = new MultiColumnListView(columns)
            {
                itemsSource = m_Groups,
                fixedItemHeight = k_RowHeight,
                sortingMode = ColumnSortingMode.Default,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.All,
                reorderable = false,
            };
            m_RootListView.style.flexGrow = 1;

            m_RootListView.columnSortingChanged += SortRoots;
            m_RootListView.selectionChanged += OnRootSelectionChanged;
        }

        static VisualElement MakeIconLabelCell()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1 } };
            var icon = new Image { name = "icon", style = { width = 16, height = 16, marginRight = 4, flexShrink = 0 } };
            var label = new Label { name = "label", style = { unityTextAlign = TextAnchor.MiddleLeft, overflow = Overflow.Hidden, flexGrow = 1 } };
            row.Add(icon);
            row.Add(label);
            return row;
        }

        void BindRootCell(VisualElement el, int index)
        {
            if (index >= m_Groups.Count) return;
            AssetImportRecord root = m_Groups[index].Root;
            var icon = el.Q<Image>("icon");
            var label = el.Q<Label>("label");
            icon.image = GetIcon(root.AssetPath);
            label.text = root.DisplayName;
            label.tooltip = root.AssetPath;
        }

        static VisualElement MakeLabelCell() => new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, overflow = Overflow.Hidden, flexGrow = 1 } };

        void SortRoots()
        {
            List<SortColumnDescription> sorts = m_RootListView.sortedColumns?.ToList();
            if (sorts == null || sorts.Count == 0) return;

            SortColumnDescription primary = sorts[0];
            bool asc = primary.direction == SortDirection.Ascending;

            m_Groups.Sort((a, b) =>
            {
                int cmp = primary.column.name switch
                {
                    k_ColRoot => string.Compare(a.Root.DisplayName, b.Root.DisplayName, System.StringComparison.OrdinalIgnoreCase),
                    k_ColAffected => a.AffectedCount.CompareTo(b.AffectedCount),
                    k_ColDuration => a.TotalImportMs.CompareTo(b.TotalImportMs),
                    k_ColLastImport => a.LastImportedAt.HasValue && b.LastImportedAt.HasValue
                        ? a.LastImportedAt.Value.CompareTo(b.LastImportedAt.Value)
                        : string.Compare(a.Root.TimeStampDisplay, b.Root.TimeStampDisplay, System.StringComparison.OrdinalIgnoreCase),
                    k_ColReason => string.Compare(a.Reason, b.Reason, System.StringComparison.OrdinalIgnoreCase),
                    _ => 0,
                };
                return asc ? cmp : -cmp;
            });

            m_RootListView.RefreshItems();
        }

        void OnRootSelectionChanged(IEnumerable<object> selection)
        {
            m_SelectedGroup = selection.FirstOrDefault() as CascadeGroup;
            RebuildTreeView();
        }

        void RebuildTreeView()
        {
            if (m_SelectedGroup == null)
            {
                m_ChainTreeView.SetRootItems(new List<TreeViewItemData<CascadeNode>>());
                m_ChainTreeView.Rebuild();
                return;
            }

            var roots = new List<TreeViewItemData<CascadeNode>>();
            TreeViewItemData<CascadeNode>? rootItem = BuildFilteredTreeItem(m_SelectedGroup.Tree, m_TreeSearchText);
            if (rootItem.HasValue)
                roots.Add(rootItem.Value);

            m_ChainTreeView.SetRootItems(roots);
            m_ChainTreeView.Rebuild();
            m_ChainTreeView.ExpandAll();
        }

        // Keeps a node if it matches the filter itself, or if any descendant does -- non-matching
        // branches with no matching descendants are pruned out entirely.
        //
        // Iterative on purpose (two passes: flatten depth-first, then fold children into parents
        // in reverse) rather than the natural recursive bottom-up formulation -- a recursive walk
        // has no depth limit, and an uncaught StackOverflowException kills the whole editor
        // process rather than just this window.
        static TreeViewItemData<CascadeNode>? BuildFilteredTreeItem(CascadeNode root, string filter)
        {
            var order = new List<CascadeNode>();
            var parentOf = new Dictionary<CascadeNode, CascadeNode>();
            var stack = new Stack<CascadeNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                CascadeNode node = stack.Pop();
                order.Add(node);
                for (int i = node.Children.Count - 1; i >= 0; i--)
                {
                    parentOf[node.Children[i]] = node;
                    stack.Push(node.Children[i]);
                }
            }

            // order is a valid pre-order sequence, so walking it in reverse guarantees every
            // node's children have already been resolved by the time we reach that node.
            var pendingChildren = new Dictionary<CascadeNode, List<TreeViewItemData<CascadeNode>>>();
            var built = new Dictionary<CascadeNode, TreeViewItemData<CascadeNode>>();
            int nextId = 0;

            for (int i = order.Count - 1; i >= 0; i--)
            {
                CascadeNode node = order[i];
                List<TreeViewItemData<CascadeNode>> children = pendingChildren.TryGetValue(node, out List<TreeViewItemData<CascadeNode>> collected)
                    ? collected
                    : new List<TreeViewItemData<CascadeNode>>();
                children.Reverse(); // collected in reverse child order; restore original order

                bool selfMatch = string.IsNullOrEmpty(filter) ||
                    node.Record.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    node.Record.AssetPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!selfMatch && children.Count == 0)
                    continue;

                var item = new TreeViewItemData<CascadeNode>(nextId++, node, children);
                built[node] = item;

                if (parentOf.TryGetValue(node, out CascadeNode parent))
                {
                    if (!pendingChildren.TryGetValue(parent, out List<TreeViewItemData<CascadeNode>> parentChildren))
                        pendingChildren[parent] = parentChildren = new List<TreeViewItemData<CascadeNode>>();
                    parentChildren.Add(item);
                }
            }

            return built.TryGetValue(root, out TreeViewItemData<CascadeNode> rootItem) ? rootItem : (TreeViewItemData<CascadeNode>?)null;
        }

        // ── Chain tree (right) ───────────────────────────────────────────────
        void BuildChainTreeView()
        {
            var columns = new Columns
            {
                new Column
                {
                    name = k_ColTreeAsset,
                    title = "Asset",
                    width = 220,
                    minWidth = 100,
                    resizable = true,
                    stretchable = true,
                    makeCell = () => MakeIconLabelCell(),
                    bindCell = BindTreeAssetCell,
                },
                new Column
                {
                    name = k_ColTreeDuration,
                    title = "Import Time",
                    width = 90,
                    minWidth = 60,
                    resizable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) =>
                    {
                        CascadeNode node = m_ChainTreeView.GetItemDataForIndex<CascadeNode>(i);
                        ((Label)el).text = FormatMs(node.Record.ImportDurationMs);
                    },
                },
                new Column
                {
                    name = k_ColTreeImporter,
                    title = "Importer",
                    width = 140,
                    minWidth = 60,
                    resizable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) =>
                    {
                        CascadeNode node = m_ChainTreeView.GetItemDataForIndex<CascadeNode>(i);
                        ((Label)el).text = node.Record.ImporterName ?? "-";
                    },
                },
                new Column
                {
                    name = k_ColTreeReason,
                    title = "Reason",
                    width = 220,
                    minWidth = 100,
                    resizable = true,
                    makeCell = MakeLabelCell,
                    bindCell = (el, i) =>
                    {
                        CascadeNode node = m_ChainTreeView.GetItemDataForIndex<CascadeNode>(i);
                        string reason = node.Record.ReasonMessages.Count > 0
                            ? node.Record.ReasonMessages[0]
                            : node.CausedBy == null
                                ? "Imported directly"
                                : $"Dependency of {node.CausedBy.DisplayName}";
                        var label = (Label)el;
                        label.text = reason;
                        label.tooltip = reason;
                    },
                },
            };

            m_ChainTreeView = new MultiColumnTreeView(columns)
            {
                fixedItemHeight = k_RowHeight,
                showAlternatingRowBackgrounds = AlternatingRowBackground.All,
            };
        }

        void BindTreeAssetCell(VisualElement el, int index)
        {
            CascadeNode node = m_ChainTreeView.GetItemDataForIndex<CascadeNode>(index);
            var icon = el.Q<Image>("icon");
            var label = el.Q<Label>("label");
            icon.image = GetIcon(node.Record.AssetPath);
            label.text = node.Record.DisplayName;
            label.tooltip = node.Record.AssetPath;
        }

        static string FormatMs(double ms) => ms >= 1000 ? $"{ms / 1000.0:0.00} s" : $"{ms:0.0} ms";
    }
}
