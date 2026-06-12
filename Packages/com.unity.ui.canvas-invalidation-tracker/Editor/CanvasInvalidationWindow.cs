using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CanvasInvalidationTracker
{
    public sealed class CanvasInvalidationWindow : EditorWindow
    {
        // ── Layout ───────────────────────────────────────────────────────────
        const float k_RowHeight      = 19f;
        const float k_ToolbarHeight  = 21f;
        const float k_ListInitWidth  = 430f;

        // Column name constants (matched in sort switch)
        const string k_ColType   = "type";
        const string k_ColFrame  = "frame";
        const string k_ColObject = "object";
        const string k_ColCanvas = "canvas";
        const string k_ColCount  = "count";

        // ── Colors ───────────────────────────────────────────────────────────
        static readonly Color k_Layout         = new Color(0.35f, 0.65f, 1.00f, 1f);
        static readonly Color k_Graphic        = new Color(0.30f, 0.90f, 0.50f, 1f);
        static readonly Color k_CanvasRenderer = new Color(1.00f, 0.60f, 0.20f, 1f);
        static readonly Color k_PlayBg      = new Color(1f,    0.85f, 0.30f, 0.08f);
        static readonly Color k_EditBg      = new Color(0.30f, 0.50f, 1.00f, 0.05f);
        static readonly Color k_DividerLine = new Color(1f,    1f,    1f,    0.10f);
        static readonly Color k_CountHigh   = new Color(1f,    0.85f, 0.25f, 1f);

        // ── State ────────────────────────────────────────────────────────────
        Vector2   m_DetailsScroll;
        Vector2   m_TraceScroll;
        int       m_SelectedId   = -1;
        int       m_TraceIndex   = 0;
        InvalidationEntry m_Selected;

        bool m_ShowLayout         = true;
        bool m_ShowGraphic        = true;
        bool m_ShowCanvasRenderer = true;

        readonly List<InvalidationEntry> m_Filtered    = new List<InvalidationEntry>();
        bool                             m_FilterDirty = true;

        MultiColumnListView m_ListView;

        // ── Menu entry ───────────────────────────────────────────────────────
        [MenuItem("Window/Analysis/Canvas Invalidation Tracker")]
        static void Open() => GetWindow<CanvasInvalidationWindow>("Canvas Invalidation Tracker");

        // ── Lifecycle ────────────────────────────────────────────────────────
        void OnEnable()
        {
            CanvasInvalidationService.Changed += OnChanged;
            m_FilterDirty = true;
        }

        void OnDisable()
        {
            CanvasInvalidationService.Changed -= OnChanged;
        }

        void OnChanged()
        {
            m_FilterDirty = true;
            RebuildFilterIfDirty();
            ApplyCurrentSort();
            m_ListView?.RefreshItems();
            ReapplySelection();
            Repaint();
        }

        // ── UI Toolkit entry point ────────────────────────────────────────────
        void CreateGUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            // Toolbar lives in an IMGUIContainer so we can reuse the existing IMGUI code
            var toolbarContainer = new IMGUIContainer(DrawToolbar);
            toolbarContainer.style.height    = k_ToolbarHeight;
            toolbarContainer.style.flexShrink = 0;
            rootVisualElement.Add(toolbarContainer);

            // Horizontal split: list on the left, details on the right
            var split = new TwoPaneSplitView(0, k_ListInitWidth, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;

            // Left pane — MultiColumnListView
            BuildListView();
            m_ListView.style.minWidth = 150;
            split.Add(m_ListView);

            // Right pane — details stay as IMGUI for now
            var detailsPane        = new VisualElement();
            detailsPane.style.flexGrow = 1;
            detailsPane.style.minWidth = 200;
            var detailsContainer   = new IMGUIContainer(DrawDetails);
            detailsContainer.style.flexGrow = 1;
            detailsPane.Add(detailsContainer);
            split.Add(detailsPane);

            rootVisualElement.Add(split);

            RebuildFilterIfDirty();
        }

        // ── MultiColumnListView construction ─────────────────────────────────
        void BuildListView()
        {
            var columns = new Columns
            {
                new Column
                {
                    name     = k_ColType,
                    title    = "Type",
                    width    = 110,
                    minWidth = 80,
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
                        lbl.style.fontSize       = 10;
                        lbl.style.paddingLeft    = 2;
                        return lbl;
                    },
                    bindCell = (el, i) =>
                        ((Label)el).text = i < m_Filtered.Count ? $"#{m_Filtered[i].FrameNumber}" : "",
                },
                new Column
                {
                    name     = k_ColObject,
                    title    = "Object",
                    width    = 130,
                    minWidth = 60,
                    resizable = true,
                    sortable  = true,
                    makeCell = () =>
                    {
                        var lbl = new Label();
                        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
                        lbl.style.flexGrow        = 1;
                        lbl.style.fontSize       = 10;
                        lbl.style.paddingLeft    = 2;
                        lbl.style.overflow       = Overflow.Hidden;
                        return lbl;
                    },
                    bindCell = (el, i) =>
                        ((Label)el).text = i < m_Filtered.Count ? m_Filtered[i].ObjectName : "",
                },
                new Column
                {
                    name     = k_ColCanvas,
                    title    = "Canvas",
                    width    = 130,
                    minWidth = 60,
                    resizable = true,
                    sortable  = true,
                    makeCell = () =>
                    {
                        var lbl = new Label();
                        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
                        lbl.style.flexGrow        = 1;
                        lbl.style.fontSize       = 10;
                        lbl.style.paddingLeft    = 2;
                        lbl.style.overflow       = Overflow.Hidden;
                        return lbl;
                    },
                    bindCell = (el, i) =>
                        ((Label)el).text = i < m_Filtered.Count ? m_Filtered[i].CanvasName : "",
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
                // Unity forces the last column to be non-resizable; this invisible spacer
                // absorbs that restriction so every visible column can be freely resized.
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
                itemsSource                  = m_Filtered,
                fixedItemHeight              = k_RowHeight,
                sortingMode                  = ColumnSortingMode.Default,
                selectionType                = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None,
                reorderable                  = false,
            };
            m_ListView.style.flexGrow = 1;

            m_ListView.columnSortingChanged += OnColumnSortingChanged;
            m_ListView.selectionChanged     += OnListSelectionChanged;
        }

        // ── Cell / header factories ──────────────────────────────────────────

        VisualElement MakeTypeCell()
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems    = Align.Center;
            root.style.flexGrow      = 1;
            root.style.overflow      = Overflow.Hidden;

            // Coloured left-edge strip (mirrors the original 4 px IMGUI strip)
            var strip = new VisualElement { name = "strip" };
            strip.style.width      = 4;
            strip.style.alignSelf  = Align.Stretch;
            strip.style.flexShrink = 0;
            strip.style.marginRight = 4;

            var badge = new Label { name = "badge" };
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.fontSize   = 10;
            badge.style.flexShrink = 0;
            badge.style.minWidth   = 52;

            var flags = new Label { name = "flags" };
            flags.style.fontSize   = 9;
            flags.style.marginLeft = 2;
            flags.style.flexShrink = 0;

            root.Add(strip);
            root.Add(badge);
            root.Add(flags);
            return root;
        }

        void BindTypeCell(VisualElement el, int index)
        {
            if (index >= m_Filtered.Count) return;
            var e = m_Filtered[index];

            var strip = el.Q<VisualElement>("strip");
            var badge = el.Q<Label>("badge");
            var flags = el.Q<Label>("flags");

            Color typeCol = e.Type switch
            {
                InvalidationType.Layout         => k_Layout,
                InvalidationType.CanvasRenderer => k_CanvasRenderer,
                _                               => k_Graphic,
            };

            strip.style.backgroundColor = typeCol;
            badge.style.color           = typeCol;
            badge.tooltip               = "";

            switch (e.Type)
            {
                case InvalidationType.Layout:
                    badge.text = "LAYOUT";
                    flags.style.display = DisplayStyle.None;
                    break;
                case InvalidationType.CanvasRenderer:
                    badge.text    = e.MethodName ?? "CR";
                    badge.tooltip = "CanvasRenderer native setter — bypasses CanvasUpdateRegistry";
                    flags.style.display = DisplayStyle.None;
                    break;
                default:
                    badge.text = "GRAPHIC";
                    if (e.DirtyFlags != GraphicDirtyFlags.None)
                    {
                        string f = "";
                        if ((e.DirtyFlags & GraphicDirtyFlags.Vertices) != 0) f += "V";
                        if ((e.DirtyFlags & GraphicDirtyFlags.Material)  != 0) f += "M";
                        flags.text          = f;
                        flags.style.display = DisplayStyle.Flex;
                    }
                    else
                    {
                        flags.style.display = DisplayStyle.None;
                    }
                    break;
            }

            // Tint the whole row for play- vs edit-mode context.
            // Walk up from the cell to the list-item row element.
            var row = el.parent;
            while (row != null && !row.ClassListContains("unity-list-view__item"))
                row = row.parent;
            if (row != null)
                row.style.backgroundColor = e.IsInPlayMode ? k_PlayBg : k_EditBg;
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
            var e   = m_Filtered[index];
            var lbl = (Label)el;
            lbl.text = e.Count.ToString();
            if (e.Count > 1)
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

            switch (primary.column.name)
            {
                case k_ColType:
                    m_Filtered.Sort((a, b) => asc
                        ? a.Type.CompareTo(b.Type)
                        : b.Type.CompareTo(a.Type));
                    break;
                case k_ColFrame:
                    m_Filtered.Sort((a, b) => asc
                        ? a.FrameNumber.CompareTo(b.FrameNumber)
                        : b.FrameNumber.CompareTo(a.FrameNumber));
                    break;
                case k_ColObject:
                    m_Filtered.Sort((a, b) => asc
                        ? string.Compare(a.ObjectName, b.ObjectName, System.StringComparison.Ordinal)
                        : string.Compare(b.ObjectName, a.ObjectName, System.StringComparison.Ordinal));
                    break;
                case k_ColCanvas:
                    m_Filtered.Sort((a, b) => asc
                        ? string.Compare(a.CanvasName, b.CanvasName, System.StringComparison.Ordinal)
                        : string.Compare(b.CanvasName, a.CanvasName, System.StringComparison.Ordinal));
                    break;
                case k_ColCount:
                    m_Filtered.Sort((a, b) => asc
                        ? a.Count.CompareTo(b.Count)
                        : b.Count.CompareTo(a.Count));
                    break;
            }
        }

        // Re-select the previously selected entry after a list rebuild or sort
        void ReapplySelection()
        {
            if (m_ListView == null || m_Selected == null) return;
            int idx = m_Filtered.IndexOf(m_Selected);
            if (idx >= 0)
                m_ListView.SetSelectionWithoutNotify(new[] { idx });
            else
            {
                m_Selected   = null;
                m_SelectedId = -1;
                m_ListView.ClearSelection();
            }
        }

        void OnListSelectionChanged(IEnumerable<object> selectedItems)
        {
            var entry = selectedItems.FirstOrDefault() as InvalidationEntry;
            if (entry != null)
                SelectEntry(entry);
            else
            {
                m_Selected   = null;
                m_SelectedId = -1;
            }
            Repaint();
        }

        // ── Filter ───────────────────────────────────────────────────────────
        void RebuildFilterIfDirty()
        {
            if (!m_FilterDirty) return;
            m_FilterDirty = false;
            m_Filtered.Clear();
            foreach (var e in CanvasInvalidationService.Entries)
            {
                if (!m_ShowLayout         && e.Type == InvalidationType.Layout)         continue;
                if (!m_ShowGraphic        && e.Type == InvalidationType.Graphic)        continue;
                if (!m_ShowCanvasRenderer && e.Type == InvalidationType.CanvasRenderer) continue;
                m_Filtered.Add(e);
            }
        }

        // ── Toolbar (IMGUI) ──────────────────────────────────────────────────
        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(48)))
                DoClear();

            bool paused = CanvasInvalidationService.IsPaused;
            if (GUILayout.Button(paused ? "▶ Resume" : "⏸ Pause",
                    EditorStyles.toolbarButton, GUILayout.Width(72)))
                CanvasInvalidationService.IsPaused = !paused;

            GUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            m_ShowLayout         = GUILayout.Toggle(m_ShowLayout,         "Layout",  EditorStyles.toolbarButton, GUILayout.Width(52));
            m_ShowGraphic        = GUILayout.Toggle(m_ShowGraphic,        "Graphic", EditorStyles.toolbarButton, GUILayout.Width(58));
            m_ShowCanvasRenderer = GUILayout.Toggle(m_ShowCanvasRenderer, "CR",      EditorStyles.toolbarButton, GUILayout.Width(34));
            if (EditorGUI.EndChangeCheck())
            {
                m_FilterDirty = true;
                RebuildFilterIfDirty();
                ApplyCurrentSort();
                m_ListView?.RefreshItems();
                ReapplySelection();
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label("Max:", EditorStyles.miniLabel, GUILayout.Width(30));
            int newMax = EditorGUILayout.IntField(CanvasInvalidationService.MaxEntries,
                EditorStyles.toolbarTextField, GUILayout.Width(52));
            if (newMax != CanvasInvalidationService.MaxEntries)
                CanvasInvalidationService.MaxEntries = newMax;

            GUILayout.Label($"{m_Filtered.Count} entries", EditorStyles.miniLabel, GUILayout.Width(72));

            bool patching  = CanvasInvalidationService.IsPatchingActive;
            var  prevColor = GUI.color;
            GUI.color = patching ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.5f, 0.2f);
            GUILayout.Label(patching ? "● Traces ON" : "● Traces OFF",
                            EditorStyles.miniLabel, GUILayout.Width(82));
            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();
        }

        void DoClear()
        {
            CanvasInvalidationService.Clear();
            m_Selected    = null;
            m_SelectedId  = -1;
            m_FilterDirty = true;
            RebuildFilterIfDirty();
            m_ListView?.RefreshItems();
            Repaint();
        }

        // ── Details panel (IMGUI) ────────────────────────────────────────────
        // Runs inside an IMGUIContainer — no BeginArea/EndArea needed.
        void DrawDetails()
        {
            if (!CanvasInvalidationService.IsReflectionReady)
            {
                EditorGUILayout.HelpBox(
                    "Reflection init failed — CanvasUpdateRegistry internal fields not found.\n" +
                    "Ensure com.unity.ugui is installed.",
                    MessageType.Error);
                return;
            }

            if (m_Selected == null)
            {
                GUILayout.FlexibleSpace();
                var centreStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                GUILayout.Label("Select an entry in the list\nto inspect its details.", centreStyle);
                GUILayout.FlexibleSpace();
                return;
            }

            var e = m_Selected;
            m_DetailsScroll = GUILayout.BeginScrollView(m_DetailsScroll);
            GUILayout.Space(6);

            // ── Title ─────────────────────────────────────────────────────
            Color typeCol = e.Type == InvalidationType.Layout ? k_Layout : k_Graphic;
            Color prev    = GUI.color;
            GUI.color = typeCol;
            GUILayout.Label(e.Type == InvalidationType.Layout
                            ? "  Layout Invalidation"
                            : "  Graphic Invalidation",
                            EditorStyles.boldLabel);
            GUI.color = prev;
            DrawDivider();
            GUILayout.Space(4);

            // ── Object ────────────────────────────────────────────────────
            DrawSectionHeader("Object");
            DrawField("Name", e.ObjectName);
            DrawField("Path", e.HierarchyPath);
            GUILayout.Space(4);

            var  go    = e.Target;
            bool alive = go != null;
            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            using (new EditorGUI.DisabledScope(!alive))
            {
                if (GUILayout.Button("Ping in Hierarchy", EditorStyles.miniButton, GUILayout.Width(130)))
                    EditorGUIUtility.PingObject(go);
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    Selection.activeGameObject = go;
                    EditorGUIUtility.PingObject(go);
                }
            }
            if (!alive)
                GUILayout.Label("  (object destroyed)", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            // ── Invalidation ──────────────────────────────────────────────
            DrawSectionHeader("Invalidation Details");
            DrawField("Type",  e.Type.ToString());
            DrawField("Count", e.Count > 1 ? $"{e.Count}×" : "1");
            DrawField("Frame", $"#{e.FrameNumber}");
            DrawField("Time",  $"{e.Time:F3} s");
            DrawField("Mode",  e.IsInPlayMode ? "Play Mode" : "Edit Mode");
            if (e.Type == InvalidationType.Graphic)
            {
                string flagStr;
                if (e.DirtyFlags == GraphicDirtyFlags.None)
                    flagStr = "(flags cleared before capture)";
                else
                {
                    var parts = new List<string>(2);
                    if ((e.DirtyFlags & GraphicDirtyFlags.Vertices) != 0) parts.Add("Vertices");
                    if ((e.DirtyFlags & GraphicDirtyFlags.Material) != 0) parts.Add("Material");
                    flagStr = string.Join(" + ", parts);
                }
                DrawField("Dirty Flags", flagStr);
            }
            GUILayout.Space(6);

            // ── Canvas ────────────────────────────────────────────────────
            DrawSectionHeader("Canvas");
            DrawField("Canvas",      e.CanvasName);
            DrawField("Render Mode", e.CanvasRenderMode);
            GUILayout.Space(6);

            // ── Components ────────────────────────────────────────────────
            DrawSectionHeader($"Components ({e.ComponentTypeNames.Length})");
            foreach (var comp in e.ComponentTypeNames)
                GUILayout.Label($"  • {comp}", EditorStyles.miniLabel);
            GUILayout.Space(6);

            // ── Call-site Stack Trace ─────────────────────────────────────
            int traceCount = e.Traces.Count;
            m_TraceIndex = Mathf.Clamp(m_TraceIndex, 0, Mathf.Max(0, traceCount - 1));

            // Header row: title + cycler when there are multiple traces
            GUILayout.BeginHorizontal();
            DrawSectionHeader(traceCount > 1 ? $"Call-site Stack Traces  ({traceCount} unique)" : "Call-site Stack Trace");
            GUILayout.FlexibleSpace();
            if (traceCount > 1)
            {
                if (GUILayout.Button("◀", EditorStyles.miniButton, GUILayout.Width(22))
                    && m_TraceIndex > 0)
                { m_TraceIndex--; m_TraceScroll = Vector2.zero; }
                GUILayout.Label($"{m_TraceIndex + 1} / {traceCount}",
                    EditorStyles.miniLabel, GUILayout.Width(42));
                if (GUILayout.Button("▶", EditorStyles.miniButton, GUILayout.Width(22))
                    && m_TraceIndex < traceCount - 1)
                { m_TraceIndex++; m_TraceScroll = Vector2.zero; }
            }
            GUILayout.EndHorizontal();

            if (!CanvasInvalidationService.IsPatchingActive)
            {
                EditorGUILayout.HelpBox(
                    "Method patching is inactive on this platform — " +
                    "stack traces are unavailable.",
                    MessageType.Warning);
            }
            else if (traceCount == 0)
            {
                if (e.Type == InvalidationType.CanvasRenderer)
                    EditorGUILayout.HelpBox(
                        $"CanvasRenderer.{e.MethodName ?? "?"} — no trace captured.\n" +
                        "This can happen when patching is inactive on this platform.",
                        MessageType.Warning);
                else
                    EditorGUILayout.HelpBox(
                        "No trace was captured for this entry.\n" +
                        "This can happen when the element was already in the queue " +
                        "before the tracker installed its patches (e.g. during startup).",
                        MessageType.Info);
            }
            else
            {
                var (currentTrace, currentFrames) = e.Traces[m_TraceIndex];
                var labelColor = EditorStyles.label.normal.textColor;

                var frameStyle = new GUIStyle(EditorStyles.label)
                {
                    font     = Font.CreateDynamicFontFromOSFont("Courier New", 11),
                    fontSize = 11,
                    wordWrap = false,
                    richText = false,
                    clipping = TextClipping.Clip,
                    padding  = new RectOffset(4, 4, 2, 2)
                };
                frameStyle.normal.textColor = labelColor;

                var linkStyle = new GUIStyle(frameStyle);
                linkStyle.normal.textColor  = new Color(0.35f, 0.65f, 1f);
                linkStyle.hover.textColor   = new Color(0.55f, 0.80f, 1f);
                linkStyle.active.textColor  = Color.white;

                float lineH = frameStyle.lineHeight + 2f;
                int   count = currentFrames != null ? currentFrames.Length : currentTrace.Split('\n').Length;
                float textH = Mathf.Max(60f, count * lineH);

                m_TraceScroll = GUILayout.BeginScrollView(
                    m_TraceScroll, GUILayout.Height(Mathf.Min(textH, 280f)));

                if (currentFrames != null)
                {
                    foreach (var f in currentFrames)
                    {
                        bool hasFile = !string.IsNullOrEmpty(f.FilePath) && f.Line > 0;
                        if (hasFile)
                        {
                            if (GUILayout.Button(f.DisplayLine, linkStyle))
                                UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(
                                    f.FilePath, f.Line, 0);
                        }
                        else
                        {
                            GUILayout.Label(f.DisplayLine, frameStyle);
                        }
                    }
                }
                else
                {
                    GUILayout.Label(currentTrace, frameStyle);
                }

                GUILayout.EndScrollView();

                if (GUILayout.Button("Copy to Clipboard", EditorStyles.miniButton,
                                     GUILayout.Width(130)))
                    GUIUtility.systemCopyBuffer = currentTrace;
            }

            GUILayout.EndScrollView();
        }

        // ── Detail helpers ───────────────────────────────────────────────────
        static void DrawSectionHeader(string title)
        {
            GUILayout.Space(2);
            GUILayout.Label($"  {title}", EditorStyles.boldLabel);
            DrawDivider();
            GUILayout.Space(3);
        }

        static void DrawDivider()
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none,
                        GUIStyle.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, k_DividerLine);
        }

        static void DrawField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(88));
            EditorGUILayout.SelectableLabel(value, EditorStyles.miniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUILayout.EndHorizontal();
        }

        // ── Selection ────────────────────────────────────────────────────────
        void SelectEntry(InvalidationEntry entry)
        {
            m_SelectedId  = entry.Id;
            m_Selected    = entry;
            m_TraceIndex  = 0;
            m_TraceScroll = Vector2.zero;
            Repaint();
        }
    }
}
