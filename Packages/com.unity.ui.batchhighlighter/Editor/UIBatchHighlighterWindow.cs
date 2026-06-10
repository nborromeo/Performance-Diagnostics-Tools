using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UIBatchHighlighter.Editor
{
    public class UIBatchHighlighterWindow : EditorWindow
    {
        // ── Reflection ────────────────────────────────────────────────────────
        static bool s_Ready;
        static Type s_ProfilerPropertyType;
        static ConstructorInfo s_ProfilerPropertyCtor;
        static MethodInfo s_SetRoot;
        static MethodInfo s_GetProfilerInfo;     // → UISystemProfilerInfo[]
        static MethodInfo s_GetBatchEntityIds;   // → EntityId[] or int[]
        static MethodInfo s_GetNameByOffset;     // (int) → string

        // UISystemProfilerInfo fields
        static FieldInfo s_IsBatch;
        static FieldInfo s_EntityIdsIndex;
        static FieldInfo s_EntityIdsCount;
        static FieldInfo s_BatchBreakingReason;
        static FieldInfo s_ObjectNameOffset;
        static FieldInfo s_Depth;               // stencil depth — increases on push, decreases on pop

        static MethodInfo s_EntityIdToObject;     // EditorUtility.EntityIdToObject(EntityId)
        static MethodInfo s_IntToEntityId;        // EntityId op_Implicit(int) — needed when IDs come back as int[]
        static bool s_UseLegacyInstanceIds;       // true when batch IDs array is int[] not EntityId[]

        // ProfilerDriver properties
        static PropertyInfo s_LastFrameIndex;
        static PropertyInfo s_ProfileEditor;   // bool — enables editor-mode profiling
        static PropertyInfo s_ProfilerEnabled; // bool — the actual record button

        // ── Data model ────────────────────────────────────────────────────────
        enum BatchType { Normal, StencilPush, StencilPop }

        sealed class BatchEntry
        {
            public readonly int Index;
            public readonly string BreakReason;
            public readonly List<GameObject> Elements;
            public readonly BatchType Type;
            public readonly int StencilDepth;

            public GameObject First => Elements.Count > 0 ? Elements[0] : null;
            public GameObject Last  => Elements.Count > 0 ? Elements[Elements.Count - 1] : null;
            public int ElementCount => Elements.Count;

            public BatchEntry(int index, string breakReason, List<GameObject> elements,
                              BatchType type, int stencilDepth)
            {
                Index        = index;
                BreakReason  = breakReason;
                Elements     = elements;
                Type         = type;
                StencilDepth = stencilDepth;
            }
        }

        sealed class CanvasEntry
        {
            public readonly string Name;
            public readonly List<BatchEntry> Batches = new List<BatchEntry>();
            public CanvasEntry(string name) { Name = name; }
        }

        readonly List<CanvasEntry> m_Canvases = new List<CanvasEntry>();
        Vector2 m_Scroll;
        bool m_Capturing;
        bool m_WasRecording;
        bool m_WasProfileEditor;
        string m_StatusMessage = "Press Capture to record and read a frame.";

        // Highlighted batch drawn in Scene and Game views
        static List<GameObject> s_HighlightBatch = new List<GameObject>();

        static readonly Color k_FirstColor = new Color(0f,  1f,  0.2f, 0.9f);  // green
        static readonly Color k_LastColor  = new Color(1f,  0.3f, 0f,  0.9f);  // red
        static readonly Color k_AllColor   = new Color(1f,  0.9f, 0f,  0.7f);  // yellow
        static readonly Color k_PushColor  = new Color(0.4f, 0.8f, 1f,  1f);   // cyan
        static readonly Color k_PopColor   = new Color(0.9f, 0.4f, 1f,  1f);   // magenta

        // GL material for Game view overlay
        static Material s_LineMat;

        // Guard against double-registration when multiple windows exist
        static int s_GameViewListenerCount;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        [MenuItem("Window/Analysis/UI Batch Highlighter")]
        static void Open() => GetWindow<UIBatchHighlighterWindow>("UI Batch Highlighter");

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            if (s_GameViewListenerCount++ == 0)
                RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            TryInitReflection();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            if (--s_GameViewListenerCount == 0)
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Canvas.willRenderCanvases -= OnCanvasWillRender;
            ClearHighlight();
        }

        // ── Reflection init ───────────────────────────────────────────────────
        static bool TryInitReflection()
        {
            if (s_Ready) return true;

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
            const BindingFlags staticPub = BindingFlags.Public | BindingFlags.Static;

            s_ProfilerPropertyType = FindType("UnityEditorInternal.ProfilerProperty");
            if (s_ProfilerPropertyType == null) return false;

            s_ProfilerPropertyCtor = s_ProfilerPropertyType.GetConstructor(Type.EmptyTypes);
            s_SetRoot              = s_ProfilerPropertyType.GetMethod("SetRoot", all);
            s_GetProfilerInfo      = s_ProfilerPropertyType.GetMethod("GetUISystemProfilerInfo", all);
            s_GetNameByOffset      = s_ProfilerPropertyType.GetMethod("GetUISystemProfilerNameByOffset", all);

            // Unity 6.4+: EntityId-based API
            s_GetBatchEntityIds = s_ProfilerPropertyType.GetMethod("GetUISystemBatchEntityIds", all);
            // Unity 6.3: legacy int instance-ID API
            if (s_GetBatchEntityIds == null)
            {
                s_GetBatchEntityIds    = s_ProfilerPropertyType.GetMethod("GetUISystemBatchInstanceIDs", all);
                s_UseLegacyInstanceIds = s_GetBatchEntityIds != null;
            }

            var infoType = FindType("UnityEditorInternal.UISystemProfilerInfo");
            if (infoType != null)
            {
                s_IsBatch             = infoType.GetField("isBatch",             all);
                s_BatchBreakingReason = infoType.GetField("batchBreakingReason", all);
                s_ObjectNameOffset    = infoType.GetField("objectNameOffset",    all);
                // Unity 6.4+
                s_EntityIdsIndex = infoType.GetField("entityIdsIndex", all);
                s_EntityIdsCount = infoType.GetField("entityIdsCount", all);
                // Unity 6.3 fallback
                if (s_EntityIdsIndex == null) s_EntityIdsIndex = infoType.GetField("instanceIDsIndex", all);
                if (s_EntityIdsCount == null) s_EntityIdsCount = infoType.GetField("instanceIDsCount", all);

                // Stencil depth — try several candidate field names
                s_Depth = infoType.GetField("depth", all)
                       ?? infoType.GetField("stencilDepth", all)
                       ?? infoType.GetField("maskDepth", all)
                       ?? infoType.GetField("renderOrder", all);

                // Log all available fields once to aid future diagnosis
                if (s_Depth == null)
                {
                    var sb = new System.Text.StringBuilder("[UIBatchHighlighter] UISystemProfilerInfo fields: ");
                    foreach (var f in infoType.GetFields(all))
                        sb.Append(f.FieldType.Name).Append(' ').Append(f.Name).Append(", ");
                    Debug.Log(sb.ToString());
                }
            }

            s_EntityIdToObject = typeof(EditorUtility).GetMethod("EntityIdToObject", staticPub);

            if (s_UseLegacyInstanceIds)
            {
                var entityIdType = FindType("UnityEngine.EntityId");
                if (entityIdType != null)
                    s_IntToEntityId = entityIdType.GetMethod("op_Implicit", staticPub | BindingFlags.NonPublic,
                        null, new[] { typeof(int) }, null);
            }

            var profilerDriverType = FindType("UnityEditorInternal.ProfilerDriver");
            s_LastFrameIndex  = profilerDriverType?.GetProperty("lastFrameIndex", staticPub);
            s_ProfileEditor   = profilerDriverType?.GetProperty("profileEditor",  staticPub);
            s_ProfilerEnabled = profilerDriverType?.GetProperty("enabled",        staticPub);

            s_Ready = s_ProfilerPropertyCtor != null
                   && s_SetRoot              != null
                   && s_GetProfilerInfo      != null
                   && s_GetBatchEntityIds    != null
                   && s_IsBatch             != null
                   && s_EntityIdsIndex      != null
                   && s_EntityIdsCount      != null;

            if (!s_Ready)
            {
                var sb = new System.Text.StringBuilder("Reflection init failed. Missing:\n");
                if (s_ProfilerPropertyType  == null) sb.AppendLine("  UnityEditorInternal.ProfilerProperty (type)");
                if (s_ProfilerPropertyCtor  == null) sb.AppendLine("  ProfilerProperty .ctor()");
                if (s_SetRoot               == null) sb.AppendLine("  ProfilerProperty.SetRoot");
                if (s_GetProfilerInfo       == null) sb.AppendLine("  ProfilerProperty.GetUISystemProfilerInfo");
                if (s_GetBatchEntityIds     == null) sb.AppendLine("  ProfilerProperty.GetUISystemBatchEntityIds");
                if (s_IsBatch              == null) sb.AppendLine("  UISystemProfilerInfo.isBatch");
                if (s_EntityIdsIndex       == null) sb.AppendLine("  UISystemProfilerInfo.entityIdsIndex");
                if (s_EntityIdsCount       == null) sb.AppendLine("  UISystemProfilerInfo.entityIdsCount");
                if (s_LastFrameIndex       == null) sb.AppendLine("  ProfilerDriver.lastFrameIndex (non-fatal)");
                if (s_ProfilerEnabled      == null) sb.AppendLine("  ProfilerDriver.enabled (non-fatal)");
                if (s_EntityIdToObject     == null) sb.AppendLine("  EditorUtility.EntityIdToObject (non-fatal)");
                Debug.LogWarning(sb.ToString());
            }

            return s_Ready;
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // ── Capture ───────────────────────────────────────────────────────────
        void StartCapture()
        {
            if (m_Capturing) return;

            if (!TryInitReflection())
            {
                m_StatusMessage = "Reflection init failed — API not found in this Unity version.";
                return;
            }

            m_WasRecording     = s_ProfilerEnabled != null && (bool)s_ProfilerEnabled.GetValue(null);
            m_WasProfileEditor = s_ProfileEditor  != null && (bool)s_ProfileEditor.GetValue(null);

            s_ProfilerEnabled?.SetValue(null, true);
            if (!Application.isPlaying)
                s_ProfileEditor?.SetValue(null, true);

            m_Capturing = true;
            m_StatusMessage = "Recording frame…";
            Repaint();

            Canvas.willRenderCanvases += OnCanvasWillRender;

            if (!Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }

        void OnCanvasWillRender()
        {
            Canvas.willRenderCanvases -= OnCanvasWillRender;
            EditorApplication.delayCall += FinishCapture;
        }

        void FinishCapture()
        {
            if (!m_WasRecording)     s_ProfilerEnabled?.SetValue(null, false);
            if (!m_WasProfileEditor) s_ProfileEditor?.SetValue(null, false);

            m_Capturing = false;
            Capture();
            Repaint();
        }

        void Capture()
        {
            m_Canvases.Clear();
            ClearHighlight();

            if (!TryInitReflection())
            {
                m_StatusMessage = "Reflection init failed — API not found in this Unity version.";
                return;
            }

            int frame = s_LastFrameIndex != null ? (int)s_LastFrameIndex.GetValue(null) : -1;
            if (frame < 0)
            {
                m_StatusMessage = "No profiler frame available. Record a frame first.";
                return;
            }

            object prop = null;
            try
            {
                prop = s_ProfilerPropertyCtor.Invoke(null);
                s_SetRoot.Invoke(prop, new object[] { frame, 0, 0 });

                var infos     = s_GetProfilerInfo.Invoke(prop, null) as Array;
                var entityIds = s_GetBatchEntityIds.Invoke(prop, null) as Array;

                if (infos == null || entityIds == null || infos.Length == 0)
                {
                    m_StatusMessage = "No UI profiler data in this frame. Make sure a Canvas is active and the Profiler has recorded a frame.";
                    return;
                }

                CanvasEntry currentCanvas = null;
                int batchIndex = 0;
                int prevDepth  = 0;

                foreach (var info in infos)
                {
                    bool isBatch = (bool)s_IsBatch.GetValue(info);

                    if (!isBatch)
                    {
                        if (currentCanvas != null && currentCanvas.Batches.Count > 0)
                            m_Canvases.Add(currentCanvas);

                        string name = s_GetNameByOffset != null
                            ? s_GetNameByOffset.Invoke(prop, new object[] { (int)s_ObjectNameOffset.GetValue(info) }) as string ?? "Canvas"
                            : "Canvas";

                        currentCanvas = new CanvasEntry(name);
                        batchIndex = 0;
                        prevDepth  = 0;
                        continue;
                    }

                    if (currentCanvas == null) continue;

                    int idxStart = (int)s_EntityIdsIndex.GetValue(info);
                    int count    = (int)s_EntityIdsCount.GetValue(info);
                    string reason = s_BatchBreakingReason.GetValue(info)?.ToString() ?? "";

                    var elements = new List<GameObject>(count);
                    for (int i = idxStart; i < idxStart + count && i < entityIds.Length; i++)
                    {
                        var go = EntityIdToGameObject(entityIds.GetValue(i));
                        if (go != null) elements.Add(go);
                    }

                    // ── Stencil push/pop detection ────────────────────────────
                    int curDepth = s_Depth != null ? (int)s_Depth.GetValue(info) : -1;
                    BatchType batchType = DetermineType(curDepth, prevDepth, elements);
                    if (curDepth >= 0) prevDepth = curDepth;

                    currentCanvas.Batches.Add(new BatchEntry(batchIndex++, reason, elements, batchType, curDepth));
                }

                if (currentCanvas != null && currentCanvas.Batches.Count > 0)
                    m_Canvases.Add(currentCanvas);

                int total = 0;
                foreach (var c in m_Canvases) total += c.Batches.Count;
                m_StatusMessage = $"Frame {frame}: found {m_Canvases.Count} canvas(es), {total} batch(es).";
            }
            catch (Exception e)
            {
                m_StatusMessage = $"Error: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                if (prop is IDisposable d) d.Dispose();
            }
        }

        /// <summary>
        /// Classify a batch as Normal, StencilPush, or StencilPop.
        /// Primary signal: depth field (if available). Fallback: Mask component on elements.
        /// </summary>
        static BatchType DetermineType(int curDepth, int prevDepth, List<GameObject> elements)
        {
            if (curDepth >= 0)
            {
                if (curDepth > prevDepth) return BatchType.StencilPush;
                if (curDepth < prevDepth) return BatchType.StencilPop;
                return BatchType.Normal;
            }

            // Fallback: if the batch contains a Mask component it's a push;
            // if elements are zero (cleanup batch) and we came from depth > 0 it's a pop.
            foreach (var go in elements)
            {
                if (go != null && go.GetComponent<UnityEngine.UI.Mask>() != null)
                    return BatchType.StencilPush;
            }
            return BatchType.Normal;
        }

        static GameObject EntityIdToGameObject(object id)
        {
            if (id == null || s_EntityIdToObject == null) return null;

            if (s_UseLegacyInstanceIds && s_IntToEntityId != null)
                id = s_IntToEntityId.Invoke(null, new[] { id });

            var obj = s_EntityIdToObject.Invoke(null, new[] { id }) as UnityEngine.Object;
            if (obj is GameObject go) return go;
            if (obj is Component c) return c.gameObject;
            return null;
        }

        // ── GUI ───────────────────────────────────────────────────────────────
        void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.HelpBox(m_StatusMessage, m_Canvases.Count > 0 ? MessageType.Info : MessageType.None);

            if (m_Canvases.Count == 0) return;

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var canvas in m_Canvases)
                DrawCanvas(canvas);
            EditorGUILayout.EndScrollView();
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            using (new EditorGUI.DisabledScope(m_Capturing))
            {
                string label = m_Capturing ? "Recording…" : "Capture Frame";
                if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(140)))
                    StartCapture();
            }

            if (GUILayout.Button("Clear Highlight", EditorStyles.toolbarButton, GUILayout.Width(110)))
                ClearHighlight();

            GUILayout.FlexibleSpace();
            DrawLegend();
            EditorGUILayout.EndHorizontal();
        }

        static void DrawLegend()
        {
            var prev = GUI.color;
            GUI.color = k_FirstColor; GUILayout.Label("■ First",  EditorStyles.toolbarButton, GUILayout.Width(50));
            GUI.color = k_AllColor;   GUILayout.Label("■ All",    EditorStyles.toolbarButton, GUILayout.Width(40));
            GUI.color = k_LastColor;  GUILayout.Label("■ Last",   EditorStyles.toolbarButton, GUILayout.Width(48));
            GUI.color = k_PushColor;  GUILayout.Label("▼ Push",   EditorStyles.toolbarButton, GUILayout.Width(48));
            GUI.color = k_PopColor;   GUILayout.Label("▲ Pop",    EditorStyles.toolbarButton, GUILayout.Width(44));
            GUI.color = prev;
        }

        void DrawCanvas(CanvasEntry canvas)
        {
            EditorGUILayout.LabelField(canvas.Name, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            foreach (var batch in canvas.Batches)
                DrawBatch(batch);

            EditorGUI.indentLevel--;
        }

        void DrawBatch(BatchEntry batch)
        {
            // Tint the box background for push/pop batches
            var prevBg = GUI.backgroundColor;
            if      (batch.Type == BatchType.StencilPush) GUI.backgroundColor = new Color(0.4f, 0.8f, 1f,  0.25f);
            else if (batch.Type == BatchType.StencilPop)  GUI.backgroundColor = new Color(0.9f, 0.4f, 1f,  0.25f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // ── Header row ───────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal();

            // Stencil badge
            if (batch.Type != BatchType.Normal)
            {
                var badgeColor = batch.Type == BatchType.StencilPush ? k_PushColor : k_PopColor;
                string badge   = batch.Type == BatchType.StencilPush ? "▼ STENCIL PUSH" : "▲ STENCIL POP";
                var prevColor  = GUI.color;
                GUI.color = badgeColor;
                GUILayout.Label(badge, EditorStyles.boldLabel, GUILayout.Width(130));
                GUI.color = prevColor;
            }

            string depthStr = batch.StencilDepth >= 0 ? $"  depth:{batch.StencilDepth}" : "";
            EditorGUILayout.LabelField(
                $"Batch {batch.Index}  ({batch.ElementCount} elements){depthStr}",
                GUILayout.ExpandWidth(true));

            if (!string.IsNullOrEmpty(batch.BreakReason))
                EditorGUILayout.LabelField(batch.BreakReason, EditorStyles.miniLabel, GUILayout.Width(180));

            EditorGUILayout.EndHorizontal();

            // ── Button row ───────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal();
            DrawElementButton("First", batch.First, k_FirstColor);
            DrawElementButton("Last",  batch.Last,  k_LastColor);

            if (GUILayout.Button("Highlight Batch", EditorStyles.miniButton, GUILayout.Width(110)))
                SetHighlight(batch.Elements);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        static void DrawElementButton(string label, GameObject go, Color color)
        {
            bool isNull = go == null;
            var prev = GUI.color;
            GUI.color = isNull ? Color.gray : color;
            if (GUILayout.Button($"{label}: {(isNull ? "(none)" : go.name)}", EditorStyles.miniButton, GUILayout.Width(200)))
            {
                if (!isNull)
                {
                    Selection.activeGameObject = go;
                    EditorGUIUtility.PingObject(go);
                    SetHighlight(new List<GameObject> { go });
                }
            }
            GUI.color = prev;
        }

        // ── Highlight ─────────────────────────────────────────────────────────
        static void SetHighlight(List<GameObject> elements)
        {
            s_HighlightBatch = elements ?? new List<GameObject>();
            SceneView.RepaintAll();
        }

        static void ClearHighlight()
        {
            s_HighlightBatch.Clear();
            SceneView.RepaintAll();
        }

        // ── Scene view overlay ────────────────────────────────────────────────
        static void OnSceneGUI(SceneView sv)
        {
            if (s_HighlightBatch == null || s_HighlightBatch.Count == 0) return;

            int last = s_HighlightBatch.Count - 1;
            for (int i = 0; i <= last; i++)
            {
                var go = s_HighlightBatch[i];
                if (go == null) continue;
                Color color = i == 0    ? k_FirstColor
                            : i == last ? k_LastColor
                            : k_AllColor;
                string lbl  = i == 0    ? "First"
                            : i == last ? "Last"
                            : null;
                DrawSceneRect(go, color, lbl);
            }
        }

        static void DrawSceneRect(GameObject go, Color color, string label)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);

            Handles.color = color;
            Handles.DrawAAPolyLine(4f, corners[0], corners[1], corners[2], corners[3], corners[0]);

            if (label != null)
            {
                var style = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = color },
                    fontSize = 11
                };
                Handles.Label(corners[1] + Vector3.up * 0.01f, $"{label}\n{go.name}", style);
            }
        }

        // ── Game view overlay (URP) ───────────────────────────────────────────
        static void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam.cameraType != CameraType.Game) return;
            if (s_HighlightBatch == null || s_HighlightBatch.Count == 0) return;

            var mat = GetLineMat();
            if (mat == null) return;

            mat.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(Matrix4x4.identity);
            GL.LoadIdentity();

            int last = s_HighlightBatch.Count - 1;
            for (int i = 0; i <= last; i++)
            {
                var go = s_HighlightBatch[i];
                if (go == null) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;

                Color color = i == 0    ? k_FirstColor
                            : i == last ? k_LastColor
                            : k_AllColor;

                DrawGLRect(cam, rt, color);
            }

            GL.PopMatrix();
        }

        static void DrawGLRect(Camera cam, RectTransform rt, Color color)
        {
            var rootCanvas = rt.GetComponentInParent<Canvas>()?.rootCanvas;
            bool isOverlay = rootCanvas != null && rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;

            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);

            GL.Begin(GL.LINE_STRIP);
            GL.Color(color);

            for (int j = 0; j <= 4; j++)
            {
                Vector3 corner = corners[j % 4];
                Vector3 ndc;

                if (isOverlay)
                {
                    ndc = new Vector3(
                        corner.x / cam.pixelWidth  * 2f - 1f,
                        corner.y / cam.pixelHeight * 2f - 1f,
                        0f);
                }
                else
                {
                    var vp = cam.WorldToViewportPoint(corner);
                    ndc = new Vector3(vp.x * 2f - 1f, vp.y * 2f - 1f, 0f);
                }

                GL.Vertex(ndc);
            }

            GL.End();
        }

        static Material GetLineMat()
        {
            if (s_LineMat != null) return s_LineMat;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            s_LineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            s_LineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            s_LineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            s_LineMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            s_LineMat.SetInt("_ZWrite",   0);
            s_LineMat.SetInt("_ZTest",    (int)UnityEngine.Rendering.CompareFunction.Always);
            return s_LineMat;
        }
    }
}
