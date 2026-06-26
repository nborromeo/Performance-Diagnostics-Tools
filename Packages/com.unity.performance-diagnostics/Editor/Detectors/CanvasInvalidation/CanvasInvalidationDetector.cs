using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// ReSharper disable once RedundantUsingDirective
using PerformanceDiagnostics;

namespace PerformanceDiagnostics.Detectors
{
    [InitializeOnLoad]
    public sealed class CanvasInvalidationDetector : IDiagnosticDetector
    {
        static readonly CanvasInvalidationDetector s_Instance = new CanvasInvalidationDetector();

        static CanvasInvalidationDetector()
        {
            DiagnosticRegistry.Register(s_Instance);
        }

        // ── Colors ───────────────────────────────────────────────────────────
        static readonly Color k_Layout         = new Color(0.35f, 0.65f, 1.00f, 1f);
        static readonly Color k_Graphic        = new Color(0.30f, 0.90f, 0.50f, 1f);
        static readonly Color k_CanvasRenderer = new Color(1.00f, 0.60f, 0.20f, 1f);
        static readonly Color k_Category       = new Color(0.35f, 0.65f, 1.00f, 1f);
        static readonly Color k_DividerLine    = new Color(1f, 1f, 1f, 0.10f);
        static readonly Color k_CountHigh      = new Color(1f, 0.85f, 0.25f, 1f);

        readonly List<DiagnosticIssue> m_Issues = new List<DiagnosticIssue>();

        CanvasInvalidationDetector()
        {
            CanvasInvalidationService.Changed += OnServiceChanged;
        }

        // ── IDiagnosticDetector ──────────────────────────────────────────────
        public string Category      => "Canvas Invalidation";
        public Color  CategoryColor => k_Category;
        public bool   IsEnabled     { get; set; } = true;

        public IReadOnlyList<DiagnosticIssue> Issues => m_Issues;

        public event Action Changed;

        void OnServiceChanged()
        {
            RebuildIssues();
            Changed?.Invoke();
        }

        void RebuildIssues()
        {
            m_Issues.Clear();
            foreach (var e in CanvasInvalidationService.Entries)
            {
                Color typeColor = e.Type switch
                {
                    InvalidationType.Layout         => k_Layout,
                    InvalidationType.CanvasRenderer => k_CanvasRenderer,
                    _                               => k_Graphic,
                };

                string issueType = e.Type switch
                {
                    InvalidationType.Layout         => "LAYOUT",
                    InvalidationType.CanvasRenderer => e.MethodName ?? "CR",
                    _                               => "GRAPHIC",
                };

                // Append dirty-flag suffix for Graphic entries
                if (e.Type == InvalidationType.Graphic && e.DirtyFlags != GraphicDirtyFlags.None)
                {
                    string flags = "";
                    if ((e.DirtyFlags & GraphicDirtyFlags.Vertices) != 0) flags += "V";
                    if ((e.DirtyFlags & GraphicDirtyFlags.Material) != 0) flags += "M";
                    issueType += $" [{flags}]";
                }

                m_Issues.Add(new DiagnosticIssue
                {
                    Id              = e.Id,
                    Count           = e.Count,
                    FrameNumber     = e.FrameNumber,
                    Time            = e.Time,
                    IsInPlayMode    = e.IsInPlayMode,
                    Category        = Category,
                    CategoryColor   = k_Category,
                    IssueType       = issueType,
                    IssueTypeColor  = typeColor,
                    ObjectName      = e.ObjectName,
                    HierarchyPath   = e.HierarchyPath,
                    Target          = e.Target,
                    ContextName     = e.CanvasName,
                    DetectorPayload = e,
                });
            }
        }

        public void Clear() => CanvasInvalidationService.Clear();

        // ── Toolbar controls ─────────────────────────────────────────────────
        public void DrawToolbarControls()
        {
            bool paused = CanvasInvalidationService.IsPaused;
            if (GUILayout.Button(paused ? "▶ Resume" : "⏸ Pause",
                    EditorStyles.toolbarButton, GUILayout.Width(72)))
                CanvasInvalidationService.IsPaused = !paused;

            bool patching  = CanvasInvalidationService.IsPatchingActive;
            var  prevColor = GUI.color;
            GUI.color = patching ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.5f, 0.2f);
            GUILayout.Label(patching ? "● Traces" : "● No Traces",
                            EditorStyles.miniLabel, GUILayout.Width(70));
            GUI.color = prevColor;
        }

        // ── Settings popup ────────────────────────────────────────────────────
        public bool    HasSettings        => true;
        public Vector2 SettingsPopupSize  => new Vector2(200f, 42f);

        public void DrawSettingsGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Max entries:", EditorStyles.miniLabel, GUILayout.Width(80));
            int newMax = EditorGUILayout.IntField(CanvasInvalidationService.MaxEntries,
                GUILayout.Width(60));
            if (newMax != CanvasInvalidationService.MaxEntries && newMax > 0)
                CanvasInvalidationService.MaxEntries = newMax;
            GUILayout.EndHorizontal();
        }

        // ── Details panel ────────────────────────────────────────────────────
        public void DrawIssueDetails(DiagnosticIssue issue, ref Vector2 scroll, ref int traceIndex, ref Vector2 traceScroll)
        {
            if (!CanvasInvalidationService.IsReflectionReady)
            {
                EditorGUILayout.HelpBox(
                    "Reflection init failed — CanvasUpdateRegistry internal fields not found.\n" +
                    "Ensure com.unity.ugui is installed.",
                    MessageType.Error);
                return;
            }

            if (issue?.DetectorPayload is not InvalidationEntry e) return;

            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Space(6);

            // ── Title ──────────────────────────────────────────────────────
            Color typeCol = e.Type == InvalidationType.Layout ? k_Layout
                          : e.Type == InvalidationType.CanvasRenderer ? k_CanvasRenderer
                          : k_Graphic;
            var prev = GUI.color;
            GUI.color = typeCol;
            string titleText = e.Type == InvalidationType.Layout   ? "  Layout Invalidation"
                             : e.Type == InvalidationType.Graphic   ? "  Graphic Invalidation"
                             : $"  CanvasRenderer.{e.MethodName ?? "?"}";
            GUILayout.Label(titleText, EditorStyles.boldLabel);
            GUI.color = prev;
            DrawDivider();
            GUILayout.Space(4);

            // ── Invalidation details ───────────────────────────────────────
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

            // ── Canvas ─────────────────────────────────────────────────────
            DrawSectionHeader("Canvas");
            DrawField("Canvas",      e.CanvasName);
            DrawField("Render Mode", e.CanvasRenderMode);
            GUILayout.Space(6);

            // ── Components ─────────────────────────────────────────────────
            if (e.ComponentTypeNames != null)
            {
                DrawSectionHeader($"Components ({e.ComponentTypeNames.Length})");
                foreach (var comp in e.ComponentTypeNames)
                    GUILayout.Label($"  • {comp}", EditorStyles.miniLabel);
                GUILayout.Space(6);
            }

            // ── Stack trace ────────────────────────────────────────────────
            int traceCount = e.Traces.Count;
            traceIndex = Mathf.Clamp(traceIndex, 0, Mathf.Max(0, traceCount - 1));

            GUILayout.BeginHorizontal();
            DrawSectionHeader(traceCount > 1
                ? $"Call-site Stack Traces  ({traceCount} unique)"
                : "Call-site Stack Trace");
            GUILayout.FlexibleSpace();
            if (traceCount > 1)
            {
                if (GUILayout.Button("◀", EditorStyles.miniButton, GUILayout.Width(22))
                    && traceIndex > 0)
                { traceIndex--; traceScroll = Vector2.zero; }
                GUILayout.Label($"{traceIndex + 1} / {traceCount}",
                    EditorStyles.miniLabel, GUILayout.Width(42));
                if (GUILayout.Button("▶", EditorStyles.miniButton, GUILayout.Width(22))
                    && traceIndex < traceCount - 1)
                { traceIndex++; traceScroll = Vector2.zero; }
            }
            GUILayout.EndHorizontal();

            if (!CanvasInvalidationService.IsPatchingActive)
            {
                EditorGUILayout.HelpBox(
                    "Method patching is inactive on this platform — stack traces unavailable.",
                    MessageType.Warning);
            }
            else if (traceCount == 0)
            {
                EditorGUILayout.HelpBox(
                    e.Type == InvalidationType.CanvasRenderer
                        ? $"CanvasRenderer.{e.MethodName ?? "?"} — no trace captured."
                        : "No trace was captured for this entry.",
                    MessageType.Info);
            }
            else
            {
                var (currentTrace, currentFrames) = e.Traces[traceIndex];
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
                linkStyle.normal.textColor = new Color(0.35f, 0.65f, 1f);
                linkStyle.hover.textColor  = new Color(0.55f, 0.80f, 1f);
                linkStyle.active.textColor = Color.white;

                float lineH   = frameStyle.lineHeight + 2f;
                int   fCount  = currentFrames != null ? currentFrames.Length : currentTrace.Split('\n').Length;
                float textH   = Mathf.Max(60f, fCount * lineH);

                traceScroll = GUILayout.BeginScrollView(
                    traceScroll, GUILayout.Height(Mathf.Min(textH, 280f)));

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

        // ── IMGUI helpers ────────────────────────────────────────────────────
        internal static void DrawSectionHeader(string title)
        {
            GUILayout.Space(2);
            GUILayout.Label($"  {title}", EditorStyles.boldLabel);
            DrawDivider();
            GUILayout.Space(3);
        }

        internal static void DrawDivider()
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none,
                        GUIStyle.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, k_DividerLine);
        }

        internal static void DrawField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(88));
            EditorGUILayout.SelectableLabel(value, EditorStyles.miniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUILayout.EndHorizontal();
        }
    }
}
