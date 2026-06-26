using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerformanceDiagnostics
{
    /// <summary>
    /// Unified editor window for all performance issue detectors.
    ///
    /// Layout
    /// ──────
    ///   Toolbar (IMGUI)
    ///     Per-detector:  [CategoryToggle] [primary action(s)] [⚙ settings popup]
    ///     Right:         [total count]
    ///   ──────────────────────────────────────────────────────────
    ///   TwoPaneSplitView
    ///     Left:  MultiColumnListView  (Type · Frame · Object · Context · Count)
    ///     Right: Details panel (common object header + detector DrawIssueDetails)
    /// </summary>
    public sealed class PerformanceDiagnosticsWindow : EditorWindow
    {
        // ── Layout ───────────────────────────────────────────────────────────
        const float k_RowHeight     = 19f;
        const float k_ToolbarHeight = 21f;
        const float k_ListInitWidth = 480f;

        // Column names
        const string k_ColType    = "type";
        const string k_ColFrame   = "frame";
        const string k_ColObject  = "object";
        const string k_ColContext = "context";
        const string k_ColCount   = "count";

        // ── Colors ───────────────────────────────────────────────────────────
        static readonly Color k_PlayBg    = new Color(1f,    0.85f, 0.30f, 0.08f);
        static readonly Color k_EditBg    = new Color(0.30f, 0.50f, 1.00f, 0.05f);
        static readonly Color k_CountHigh = new Color(1f,    0.85f, 0.25f, 1f);

        // ── State ────────────────────────────────────────────────────────────
        MultiColumnListView m_ListView;
        readonly List<DiagnosticIssue> m_Filtered    = new List<DiagnosticIssue>();
        bool                           m_FilterDirty = true;

        DiagnosticIssue m_Selected;
        Vector2         m_DetailsScroll;
        int             m_TraceIndex;
        Vector2         m_TraceScroll;

        // ── Menu ─────────────────────────────────────────────────────────────
        [MenuItem("Window/Analysis/Performance Diagnostics")]
        static void Open() =>
            GetWindow<PerformanceDiagnosticsWindow>("Performance Diagnostics");

        // ── Lifecycle ────────────────────────────────────────────────────────
        void OnEnable()
        {
            foreach (var d in DiagnosticRegistry.Detectors)
                d.Changed += OnDetectorChanged;

            DiagnosticRegistry.DetectorListChanged += OnDetectorListChanged;
            m_FilterDirty = true;
        }

        void OnDisable()
        {
            foreach (var d in DiagnosticRegistry.Detectors)
                d.Changed -= OnDetectorChanged;

            DiagnosticRegistry.DetectorListChanged -= OnDetectorListChanged;
        }

        void OnDetectorChanged()
        {
            m_FilterDirty = true;
            RebuildFilterIfDirty();
            ApplyCurrentSort();
            m_ListView?.RefreshItems();
            ReapplySelection();
            Repaint();
        }

        void OnDetectorListChanged()
        {
            foreach (var d in DiagnosticRegistry.Detectors)
            {
                d.Changed -= OnDetectorChanged;
                d.Changed += OnDetectorChanged;
            }
            m_FilterDirty = true;
            rootVisualElement.Clear();
            CreateGUI();
        }

        // ── UI Toolkit entry point ────────────────────────────────────────────
        public void CreateGUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            var toolbarContainer = new IMGUIContainer(DrawToolbar);
            toolbarContainer.style.height     = k_ToolbarHeight;
            toolbarContainer.style.flexShrink = 0;
            rootVisualElement.Add(toolbarContainer);

            var split = new TwoPaneSplitView(0, k_ListInitWidth, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;

            BuildListView();
            m_ListView.style.minWidth = 150;
            split.Add(m_ListView);

            var detailsPane = new VisualElement();
            detailsPane.style.flexGrow = 1;
            detailsPane.style.minWidth = 200;
            var detailsContainer = new IMGUIContainer(DrawDetails);
            detailsContainer.style.flexGrow = 1;
            detailsPane.Add(detailsContainer);
            split.Add(detailsPane);

            rootVisualElement.Add(split);

            RebuildFilterIfDirty();
        }

        // ── List view ────────────────────────────────────────────────────────
        void BuildListView()
        {
            var columns = new Columns
            {
                new Column
                {
                    name      = k_ColType,
                    title     = "Type",
                    width     = 140,
                    minWidth  = 90,
                    resizable = true,
                    sortable  = true,
                    makeCell  = MakeTypeCell,
                    bindCell  = BindTypeCell,
                },
                new Column
                {
                    name     = k_ColFrame,
                    title    = "Frame",
                    width    = 62,
                    minWidth = 40,
                    resizable = true,
                    sortable  = true,
                    makeCell = () =>
                    {
                        var lbl = new Label();
                        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
                        lbl.style.flexGrow        = 1;
                        lbl.style.fontSize        = 10;
                        lbl.style.paddingLeft     = 2;
                        return lbl;
                    },
                    bindCell = (el, i) =>
                        ((Label)el).text = i < m_Filtered.Count
                            ? (m_Filtered[i].FrameNumber > 0 ? $"#{m_Filtered[i].FrameNumber}" : "—")
                            : "",
                },
                new Column
                {
                    name     = k_ColObject,
                    title    = "Object",
                    width    = 140,
                    minWidth = 60,
                    resizable = true,
                    sortable  = true,
                    makeCell = () =>
                    {
                        var lbl = new Label();
                        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
                        lbl.style.flexGrow        = 1;
                        lbl.style.fontSize        = 10;
                        lbl.style.paddingLeft     = 2;
                        lbl.style.overflow        = Overflow.Hidden;
                        return lbl;
                    },
                    bindCell = (el, i) =>
                        ((Label)el).text = i < m_Filtered.Count ? m_Filtered[i].ObjectName : "",
                },
                new Column
                {
                    name     = k_ColContext,
                    title    = "Context",
                    width    = 120,
                    minWidth = 50,
                    resizable = true,
                    sortable  = true,
                    makeCell = () =>
                    {
                        var lbl = new Label();
                        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
                        lbl.style.flexGrow        = 1;
                        lbl.style.fontSize        = 10;
                        lbl.style.paddingLeft     = 2;
                        lbl.style.overflow        = Overflow.Hidden;
                        return lbl;
                    },
                    bindCell = (el, i) =>
                        ((Label)el).text = i < m_Filtered.Count ? m_Filtered[i].ContextName : "",
                },
                new Column
                {
                    name     = k_ColCount,
                    title    = "Count",
                    width    = 52,
                    minWidth = 30,
                    resizable = true,
                    sortable  = true,
                    makeCell  = MakeCountCell,
                    bindCell  = BindCountCell,
                },
                new Column
                {
                    name     = "__spacer",
                    title    = "",
                    width    = 0,
                    minWidth = 0,
                    resizable = false,
                    sortable  = false,
                    makeCell  = () => new VisualElement(),
                    bindCell  = (_, _) => { },
                },
            };

            m_ListView = new MultiColumnListView(columns)
            {
                itemsSource                   = m_Filtered,
                fixedItemHeight               = k_RowHeight,
                sortingMode                   = ColumnSortingMode.Default,
                selectionType                 = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None,
                reorderable                   = false,
            };
            m_ListView.style.flexGrow = 1;

            m_ListView.columnSortingChanged += OnColumnSortingChanged;
            m_ListView.selectionChanged     += OnListSelectionChanged;
        }

        // ── Cell factories ────────────────────────────────────────────────────
        static VisualElement MakeTypeCell()
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems    = Align.Center;
            root.style.flexGrow      = 1;
            root.style.overflow      = Overflow.Hidden;

            var strip = new VisualElement { name = "strip" };
            strip.style.width       = 4;
            strip.style.alignSelf   = Align.Stretch;
            strip.style.flexShrink  = 0;
            strip.style.marginRight = 4;

            // Category abbreviation prefix (e.g. "UI", "PHY", "FONT")
            var catLabel = new Label { name = "cat" };
            catLabel.style.fontSize    = 8;
            catLabel.style.flexShrink  = 0;
            catLabel.style.marginRight = 3;

            // Issue type badge (e.g. "LAYOUT", "Transform", "Atlas Update")
            var badge = new Label { name = "badge" };
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.fontSize   = 10;
            badge.style.flexShrink = 1;
            badge.style.overflow   = Overflow.Hidden;

            root.Add(strip);
            root.Add(catLabel);
            root.Add(badge);
            return root;
        }

        void BindTypeCell(VisualElement el, int index)
        {
            if (index >= m_Filtered.Count) return;
            var issue = m_Filtered[index];

            var strip    = el.Q<VisualElement>("strip");
            var catLabel = el.Q<Label>("cat");
            var badge    = el.Q<Label>("badge");

            Color stripColor = issue.CategoryColor;
            Color badgeColor = issue.IssueTypeColor != default ? issue.IssueTypeColor : issue.CategoryColor;

            strip.style.backgroundColor = stripColor;
            catLabel.style.color = new Color(stripColor.r, stripColor.g, stripColor.b, 0.65f);
            catLabel.text        = CategoryAbbreviation(issue.Category);
            badge.style.color    = badgeColor;
            badge.text           = issue.IssueType;

            var row = el.parent;
            while (row != null && !row.ClassListContains("unity-list-view__item"))
                row = row.parent;
            if (row != null)
                row.style.backgroundColor = issue.IsInPlayMode ? k_PlayBg : k_EditBg;
        }

        static string CategoryAbbreviation(string category)
        {
            if (string.IsNullOrEmpty(category)) return "";
            var parts = category.Split(' ');
            return parts.Length == 1
                ? (parts[0].Length > 4 ? parts[0][..4].ToUpper() : parts[0].ToUpper())
                : string.Concat(parts.Take(4).Select(p => p.Length > 0 ? char.ToUpper(p[0]).ToString() : ""));
        }

        static VisualElement MakeCountCell()
        {
            var lbl = new Label();
            lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            lbl.style.alignSelf      = Align.Stretch;
            lbl.style.fontSize       = 10;
            return lbl;
        }

        void BindCountCell(VisualElement el, int index)
        {
            if (index >= m_Filtered.Count) return;
            var issue = m_Filtered[index];
            var lbl   = (Label)el;
            lbl.text = issue.Count.ToString();
            if (issue.Count > 1)
            {
                lbl.style.color                   = k_CountHigh;
                lbl.style.unityFontStyleAndWeight  = FontStyle.Bold;
            }
            else
            {
                lbl.style.color                   = new StyleColor(StyleKeyword.Null);
                lbl.style.unityFontStyleAndWeight  = FontStyle.Normal;
            }
        }

        // ── Sorting ──────────────────────────────────────────────────────────
        void OnColumnSortingChanged()
        {
            ApplyCurrentSort();
            m_ListView.RefreshItems();
            ReapplySelection();
        }

        void ApplyCurrentSort()
        {
            if (m_ListView == null) return;
            var descs = m_ListView.sortedColumns?.ToList();
            if (descs == null || descs.Count == 0) return;

            var  primary = descs[0];
            bool asc     = primary.direction == SortDirection.Ascending;

            m_Filtered.Sort((a, b) =>
            {
                int cmp = primary.column.name switch
                {
                    k_ColType    => string.Compare(a.IssueType,   b.IssueType,   System.StringComparison.Ordinal),
                    k_ColFrame   => a.FrameNumber.CompareTo(b.FrameNumber),
                    k_ColObject  => string.Compare(a.ObjectName,  b.ObjectName,  System.StringComparison.Ordinal),
                    k_ColContext => string.Compare(a.ContextName, b.ContextName, System.StringComparison.Ordinal),
                    k_ColCount   => a.Count.CompareTo(b.Count),
                    _            => 0,
                };
                return asc ? cmp : -cmp;
            });
        }

        void ReapplySelection()
        {
            if (m_ListView == null || m_Selected == null) return;
            int idx = m_Filtered.IndexOf(m_Selected);
            if (idx >= 0)
                m_ListView.SetSelectionWithoutNotify(new[] { idx });
            else
            {
                m_Selected = null;
                m_ListView.ClearSelection();
            }
        }

        void OnListSelectionChanged(IEnumerable<object> selectedItems)
        {
            var issue = selectedItems.FirstOrDefault() as DiagnosticIssue;
            if (issue != null)
            {
                if (m_Selected != issue)
                {
                    m_TraceIndex  = 0;
                    m_TraceScroll = Vector2.zero;
                }
                m_Selected      = issue;
                m_DetailsScroll = Vector2.zero;

                if (issue.Target != null)
                {
                    Selection.activeGameObject = issue.Target;
                    EditorGUIUtility.PingObject(issue.Target);
                }
            }
            else
            {
                m_Selected = null;
            }
            Repaint();
        }

        // ── Filter ───────────────────────────────────────────────────────────
        void RebuildFilterIfDirty()
        {
            if (!m_FilterDirty) return;
            m_FilterDirty = false;
            m_Filtered.Clear();
            foreach (var detector in DiagnosticRegistry.Detectors)
            {
                if (!detector.IsEnabled) continue;
                m_Filtered.AddRange(detector.Issues);
            }
        }

        // ── Toolbar ──────────────────────────────────────────────────────────
        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Clear All", EditorStyles.toolbarButton, GUILayout.Width(62)))
                DoClearAll();

            GUILayout.Space(4);

            foreach (var detector in DiagnosticRegistry.Detectors)
            {
                // Category toggle
                EditorGUI.BeginChangeCheck();
                bool enabled = GUILayout.Toggle(detector.IsEnabled, detector.Category,
                    EditorStyles.toolbarButton, GUILayout.Width(MeasureCategoryWidth(detector.Category)));
                if (EditorGUI.EndChangeCheck())
                {
                    detector.IsEnabled = enabled;
                    m_FilterDirty      = true;
                    RebuildFilterIfDirty();
                    ApplyCurrentSort();
                    m_ListView?.RefreshItems();
                    ReapplySelection();
                }

                GUILayout.Space(2);

                if (detector.IsEnabled)
                    detector.DrawToolbarControls();

                // Settings popup button (⚙) — only shown when detector has secondary settings
                if (detector.HasSettings)
                {
                    bool clicked = GUILayout.Button("⚙", EditorStyles.toolbarButton, GUILayout.Width(18));
                    var btnRect  = GUILayoutUtility.GetLastRect();
                    if (clicked)
                    {
                        var screenPos  = GUIUtility.GUIToScreenPoint(new Vector2(btnRect.x, btnRect.yMax));
                        var screenRect = new Rect(screenPos.x, screenPos.y, btnRect.width, 0);
                        UnityEditor.PopupWindow.Show(screenRect, new SettingsPopup(detector));
                    }
                }

                GUILayout.Space(8);
            }

            GUILayout.FlexibleSpace();

            int total = m_Filtered.Count;
            GUILayout.Label($"{total} issue{(total != 1 ? "s" : "")}",
                            EditorStyles.miniLabel, GUILayout.Width(68));

            EditorGUILayout.EndHorizontal();
        }

        static float MeasureCategoryWidth(string category)
        {
            float w = EditorStyles.toolbarButton.CalcSize(new GUIContent(category)).x;
            return Mathf.Clamp(w, 60, 200);
        }

        void DoClearAll()
        {
            foreach (var d in DiagnosticRegistry.Detectors)
                d.Clear();

            m_Selected    = null;
            m_FilterDirty = true;
            RebuildFilterIfDirty();
            m_ListView?.RefreshItems();
            Repaint();
        }

        // ── Settings popup ────────────────────────────────────────────────────
        sealed class SettingsPopup : PopupWindowContent
        {
            readonly IDiagnosticDetector m_Detector;

            internal SettingsPopup(IDiagnosticDetector detector) => m_Detector = detector;

            public override Vector2 GetWindowSize() => m_Detector.SettingsPopupSize;

            public override void OnGUI(Rect rect)
            {
                GUILayout.BeginArea(new Rect(6, 6, rect.width - 12, rect.height - 12));
                m_Detector.DrawSettingsGUI();
                GUILayout.EndArea();
            }

            public override void OnOpen() { }
            public override void OnClose() { }
        }

        // ── Details panel ────────────────────────────────────────────────────
        void DrawDetails()
        {
            if (m_Selected == null)
            {
                GUILayout.FlexibleSpace();
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                GUILayout.Label("Select an issue in the list\nto inspect its details.", style);
                GUILayout.FlexibleSpace();
                return;
            }

            var issue    = m_Selected;
            var detector = DiagnosticRegistry.Detectors
                .FirstOrDefault(d => d.Category == issue.Category);

            if (detector == null)
            {
                GUILayout.Label("Unknown detector.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            DrawCommonHeader(issue);
            detector.DrawIssueDetails(issue, ref m_DetailsScroll, ref m_TraceIndex, ref m_TraceScroll);
        }

        void DrawCommonHeader(DiagnosticIssue issue)
        {
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            GUILayout.Label("Object", EditorStyles.boldLabel);
            GUILayout.EndHorizontal();

            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(1), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(1f, 1f, 1f, 0.10f));
            GUILayout.Space(3);

            DrawHeaderField("Name", issue.ObjectName);
            DrawHeaderField("Path", issue.HierarchyPath ?? issue.ObjectName);

            GUILayout.Space(4);
            bool alive = issue.Target != null;
            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            using (new EditorGUI.DisabledScope(!alive))
            {
                if (GUILayout.Button("Ping in Hierarchy", EditorStyles.miniButton, GUILayout.Width(130)))
                    EditorGUIUtility.PingObject(issue.Target);
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    Selection.activeGameObject = issue.Target;
                    EditorGUIUtility.PingObject(issue.Target);
                }
            }
            if (!alive)
                GUILayout.Label("  (object destroyed)", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
        }

        static void DrawHeaderField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(88));
            EditorGUILayout.SelectableLabel(value ?? "", EditorStyles.miniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUILayout.EndHorizontal();
        }
    }
}
