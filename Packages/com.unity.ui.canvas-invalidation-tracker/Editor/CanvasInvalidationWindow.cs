using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CanvasInvalidationTracker
{
    public sealed class CanvasInvalidationWindow : EditorWindow
    {
        // ── Layout ───────────────────────────────────────────────────────────
        const float k_ListWidth      = 430f;
        const float k_SplitterWidth  = 3f;
        const float k_RowHeight      = 22f;
        const float k_BadgeWidth     = 70f;
        const float k_FlagsWidth     = 24f;
        const float k_FrameWidth     = 54f;
        const float k_CanvasWidth    = 120f;
        const float k_CountWidth     = 36f;
        const float k_HeaderHeight   = k_RowHeight;
        const float k_ToolbarHeight  = 21f;

        // ── Colors ───────────────────────────────────────────────────────────
        static readonly Color k_Layout      = new Color(0.35f, 0.65f, 1.00f, 1f);
        static readonly Color k_Graphic     = new Color(0.30f, 0.90f, 0.50f, 1f);
        static readonly Color k_Selected    = new Color(0.24f, 0.48f, 0.90f, 1f);
        static readonly Color k_RowOdd      = new Color(0f,    0f,    0f,    0.05f);
        static readonly Color k_PlayBg      = new Color(1f,    0.85f, 0.30f, 0.08f);
        static readonly Color k_EditBg      = new Color(0.30f, 0.50f, 1.00f, 0.05f);
        static readonly Color k_Strip       = new Color(0f,    0f,    0f,    0.25f);
        static readonly Color k_HeaderBg    = new Color(0f,    0f,    0f,    0.20f);
        static readonly Color k_Separator   = new Color(1f,    1f,    1f,    0.08f);
        static readonly Color k_DividerLine = new Color(1f,    1f,    1f,    0.10f);

        // ── State ────────────────────────────────────────────────────────────
        Vector2   m_ListScroll;
        Vector2   m_DetailsScroll;
        Vector2   m_TraceScroll;
        int       m_SelectedId = -1;
        InvalidationEntry m_Selected;

        bool m_ShowLayout  = true;
        bool m_ShowGraphic = true;

        readonly List<InvalidationEntry> m_Filtered   = new List<InvalidationEntry>();
        bool                             m_FilterDirty = true;

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
            Repaint();
        }

        // ── Root GUI ─────────────────────────────────────────────────────────
        void OnGUI()
        {
            DrawToolbar();

            if (!CanvasInvalidationService.IsReflectionReady)
            {
                EditorGUILayout.HelpBox(
                    "Reflection init failed — CanvasUpdateRegistry internal fields not found.\n" +
                    "Ensure com.unity.ugui is installed.",
                    MessageType.Error);
                return;
            }

            RebuildFilterIfDirty();

            float y    = k_ToolbarHeight;
            float h    = position.height - y;
            float detW = position.width - k_ListWidth - k_SplitterWidth;

            var listRect    = new Rect(0,                              y, k_ListWidth,     h);
            var splitRect   = new Rect(k_ListWidth,                    y, k_SplitterWidth, h);
            var detailsRect = new Rect(k_ListWidth + k_SplitterWidth,  y, Mathf.Max(1, detW), h);

            DrawList(listRect);
            EditorGUI.DrawRect(splitRect, k_Strip);
            DrawDetails(detailsRect);
        }

        // ── Toolbar ──────────────────────────────────────────────────────────
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
            m_ShowLayout  = GUILayout.Toggle(m_ShowLayout,  "Layout",  EditorStyles.toolbarButton, GUILayout.Width(52));
            m_ShowGraphic = GUILayout.Toggle(m_ShowGraphic, "Graphic", EditorStyles.toolbarButton, GUILayout.Width(58));
            if (EditorGUI.EndChangeCheck()) m_FilterDirty = true;

            GUILayout.FlexibleSpace();

            GUILayout.Label("Max:", EditorStyles.miniLabel, GUILayout.Width(30));
            int newMax = EditorGUILayout.IntField(CanvasInvalidationService.MaxEntries,
                EditorStyles.toolbarTextField, GUILayout.Width(52));
            if (newMax != CanvasInvalidationService.MaxEntries)
                CanvasInvalidationService.MaxEntries = newMax;

            GUILayout.Label($"{m_Filtered.Count} entries", EditorStyles.miniLabel, GUILayout.Width(72));

            // Patching status
            bool patching = CanvasInvalidationService.IsPatchingActive;
            var prevColor = GUI.color;
            GUI.color = patching ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.5f, 0.2f);
            GUILayout.Label(patching ? "● Traces ON" : "● Traces OFF",
                            EditorStyles.miniLabel, GUILayout.Width(82));
            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();
        }

        void DoClear()
        {
            CanvasInvalidationService.Clear();
            m_Selected   = null;
            m_SelectedId = -1;
            m_FilterDirty = true;
        }

        // ── Filter ───────────────────────────────────────────────────────────
        void RebuildFilterIfDirty()
        {
            if (!m_FilterDirty) return;
            m_FilterDirty = false;
            m_Filtered.Clear();
            foreach (var e in CanvasInvalidationService.Entries)
            {
                if (!m_ShowLayout  && e.Type == InvalidationType.Layout)  continue;
                if (!m_ShowGraphic && e.Type == InvalidationType.Graphic) continue;
                m_Filtered.Add(e);
            }
        }

        // ── List panel ───────────────────────────────────────────────────────
        void DrawList(Rect rect)
        {
            // Header row
            var headerRect = new Rect(rect.x, rect.y, rect.width, k_HeaderHeight);
            DrawListHeader(headerRect);

            // Body
            var bodyRect = new Rect(rect.x, rect.y + k_HeaderHeight, rect.width,
                                    rect.height - k_HeaderHeight);

            if (m_Filtered.Count == 0)
            {
                GUI.Label(bodyRect, "  No invalidations captured yet.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            float contentH = m_Filtered.Count * k_RowHeight;
            var viewRect   = new Rect(0, 0, bodyRect.width - 14f, contentH);

            m_ListScroll = GUI.BeginScrollView(bodyRect, m_ListScroll, viewRect);

            // Culling: only draw visible rows
            int firstVisible = Mathf.Max(0, (int)(m_ListScroll.y / k_RowHeight));
            int lastVisible  = Mathf.Min(m_Filtered.Count - 1,
                                         (int)((m_ListScroll.y + bodyRect.height) / k_RowHeight) + 1);

            for (int i = firstVisible; i <= lastVisible; i++)
                DrawListRow(new Rect(0, i * k_RowHeight, viewRect.width, k_RowHeight), m_Filtered[i], i);

            GUI.EndScrollView();
        }

        void DrawListHeader(Rect r)
        {
            EditorGUI.DrawRect(r, k_HeaderBg);
            float x = r.x + 8f;
            DrawHeaderLabel(x,                                              r, "Type",   k_BadgeWidth + k_FlagsWidth);
            DrawHeaderLabel(x + k_BadgeWidth + k_FlagsWidth,                r, "Frame",  k_FrameWidth);
            DrawHeaderLabel(x + k_BadgeWidth + k_FlagsWidth + k_FrameWidth, r, "Object",
                            r.width - k_BadgeWidth - k_FlagsWidth - k_FrameWidth - k_CanvasWidth - k_CountWidth - 16f);
            DrawHeaderLabel(r.xMax - k_CanvasWidth - k_CountWidth,          r, "Canvas", k_CanvasWidth);
            DrawHeaderLabel(r.xMax - k_CountWidth,                          r, "Count",  k_CountWidth);
        }

        static void DrawHeaderLabel(float x, Rect row, string text, float w)
            => GUI.Label(new Rect(x, row.y + 3f, w, 16f), text, EditorStyles.boldLabel);

        void DrawListRow(Rect r, InvalidationEntry e, int index)
        {
            bool selected = e.Id == m_SelectedId;

            // Background
            if (selected)
                EditorGUI.DrawRect(r, k_Selected);
            else
            {
                Color bg = e.IsInPlayMode ? k_PlayBg : k_EditBg;
                if (index % 2 == 1) { bg.a += 0.04f; }
                EditorGUI.DrawRect(r, bg);
                if (index % 2 == 1) EditorGUI.DrawRect(r, k_RowOdd);
            }

            // Left type-colour strip
            EditorGUI.DrawRect(new Rect(r.x, r.y, 4f, r.height),
                               e.Type == InvalidationType.Layout ? k_Layout : k_Graphic);

            float x  = r.x + 8f;
            float ty = r.y + 4f;

            // Type badge
            Color prev = GUI.color;
            GUI.color = e.Type == InvalidationType.Layout ? k_Layout : k_Graphic;
            GUI.Label(new Rect(x, ty, k_BadgeWidth, 14f),
                      e.Type == InvalidationType.Layout ? "LAYOUT" : "GRAPHIC",
                      EditorStyles.miniBoldLabel);
            GUI.color = prev;

            // Dirty flags (V / M) for graphic entries
            if (e.Type == InvalidationType.Graphic && e.DirtyFlags != GraphicDirtyFlags.None)
            {
                string flags = "";
                if ((e.DirtyFlags & GraphicDirtyFlags.Vertices) != 0) flags += "V";
                if ((e.DirtyFlags & GraphicDirtyFlags.Material) != 0) flags += "M";
                GUI.Label(new Rect(x + k_BadgeWidth, ty, k_FlagsWidth, 14f), flags,
                          EditorStyles.miniLabel);
            }

            // Frame
            GUI.Label(new Rect(x + k_BadgeWidth + k_FlagsWidth, ty, k_FrameWidth, 14f),
                      $"#{e.FrameNumber}", EditorStyles.miniLabel);

            // Object name (truncated)
            float nameX = x + k_BadgeWidth + k_FlagsWidth + k_FrameWidth;
            float nameW = r.width - k_BadgeWidth - k_FlagsWidth - k_FrameWidth - k_CanvasWidth - k_CountWidth - 16f;
            GUI.Label(new Rect(nameX, ty, nameW, 14f), e.ObjectName, EditorStyles.miniLabel);

            // Canvas
            GUI.Label(new Rect(r.xMax - k_CanvasWidth - k_CountWidth + 4f, ty, k_CanvasWidth - 8f, 14f),
                      e.CanvasName, EditorStyles.miniLabel);

            // Count
            if (e.Count > 1)
            {
                var countPrev = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.25f, 1f);
                GUI.Label(new Rect(r.xMax - k_CountWidth + 2f, ty, k_CountWidth - 4f, 14f),
                          e.Count.ToString(), EditorStyles.miniBoldLabel);
                GUI.color = countPrev;
            }
            else
            {
                GUI.Label(new Rect(r.xMax - k_CountWidth + 2f, ty, k_CountWidth - 4f, 14f),
                          "1", EditorStyles.miniLabel);
            }

            // Click handler
            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            {
                SelectEntry(e);
                Event.current.Use();
            }
        }

        // ── Details panel ────────────────────────────────────────────────────
        void DrawDetails(Rect rect)
        {
            GUILayout.BeginArea(rect);

            if (m_Selected == null)
            {
                var centreStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                GUILayout.FlexibleSpace();
                GUILayout.Label("Select an entry in the list\nto inspect its details.", centreStyle);
                GUILayout.FlexibleSpace();
                GUILayout.EndArea();
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

            // Action buttons
            var  go    = e.Target;   // Unity fake-null if the object was destroyed
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
                    var parts = new System.Collections.Generic.List<string>(2);
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
            DrawSectionHeader("Call-site Stack Trace");

            if (!CanvasInvalidationService.IsPatchingActive)
            {
                EditorGUILayout.HelpBox(
                    "Method patching is inactive on this platform — " +
                    "stack traces are unavailable.",
                    MessageType.Warning);
            }
            else if (string.IsNullOrEmpty(e.StackTrace))
            {
                EditorGUILayout.HelpBox(
                    "No trace was captured for this entry.\n" +
                    "This can happen when the element was already in the queue " +
                    "before the tracker installed its patches (e.g. during startup).",
                    MessageType.Info);
            }
            else
            {
                // Monospace scrollable text block.
                // Rebuilt each frame so the text color always tracks the current
                // Editor skin (dark / light mode switches invalidate cached styles).
                var labelColor = EditorStyles.label.normal.textColor;
                var m_TraceStyle = new GUIStyle(EditorStyles.label)
                {
                    font     = Font.CreateDynamicFontFromOSFont("Courier New", 11),
                    fontSize = 11,
                    wordWrap = false,
                    richText = false,
                    clipping = TextClipping.Clip,
                    padding  = new RectOffset(4, 4, 2, 2)
                };
                m_TraceStyle.normal.textColor   = labelColor;
                m_TraceStyle.focused.textColor  = labelColor;
                m_TraceStyle.hover.textColor    = labelColor;
                m_TraceStyle.active.textColor   = labelColor;

                float lineH    = m_TraceStyle.lineHeight + 2f;
                int   lines    = e.StackTrace.Split('\n').Length;
                float textH    = Mathf.Max(60f, lines * lineH);

                m_TraceScroll = GUILayout.BeginScrollView(
                    m_TraceScroll,
                    GUILayout.Height(Mathf.Min(textH, 280f)));
                GUILayout.Label(e.StackTrace, m_TraceStyle);
                GUILayout.EndScrollView();

                if (GUILayout.Button("Copy to Clipboard", EditorStyles.miniButton,
                                     GUILayout.Width(130)))
                    GUIUtility.systemCopyBuffer = e.StackTrace;
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
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
            m_TraceScroll = Vector2.zero;

            var go = entry.Target;   // Unity fake-null if destroyed
            if (go != null)
            {
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
            }

            Repaint();
        }
    }
}
