using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

using PerformanceDiagnostics;

namespace PerformanceDiagnostics.Detectors
{
    /// <summary>
    /// Detects static colliders (no Rigidbody in parent chain) that move, toggle,
    /// or have their GO created/destroyed/activated/deactivated — events that trigger
    /// a physics broadphase rebuild.
    /// </summary>
    [InitializeOnLoad]
    public sealed class StaticRebuildDetector : IDiagnosticDetector
    {
        static readonly StaticRebuildDetector s_Instance = new StaticRebuildDetector();

        static StaticRebuildDetector()
        {
            DiagnosticRegistry.Register(s_Instance);
        }

        // ── Snapshot types ────────────────────────────────────────────────────
        struct TransformSnapshot
        {
            public string     name;
            public Vector3    position;
            public Quaternion rotation;
            public Vector3    scale;
            public bool       activeInHierarchy;
        }

        struct ColliderSnapshot
        {
            public GameObject go;
            public string     goName;
            public bool       enabled;
        }

        public enum ChangeReason
        {
            Transform,
            ColliderAdded, ColliderRemoved, ColliderEnabled, ColliderDisabled,
            GOActivated, GODeactivated, GOCreated, GODestroyed,
        }

        // ── Accumulated data ──────────────────────────────────────────────────
        public sealed class GOEntry
        {
            public GameObject go;
            public string     goName;
            public int        FrameNumber;

            public int transformCount;
            public int posCount, rotCount, sclCount;
            public int colliderAddedCount;
            public int colliderRemovedCount;
            public int colliderEnabledCount;
            public int colliderDisabledCount;
            public int goActivatedCount;
            public int goDeactivatedCount;
            public int goCreatedCount;
            public int goDestroyedCount;

            public int TotalCount =>
                transformCount + colliderAddedCount + colliderRemovedCount +
                colliderEnabledCount + colliderDisabledCount +
                goActivatedCount + goDeactivatedCount +
                goCreatedCount + goDestroyedCount;

            public string DominantType()
            {
                if (transformCount > 0)       return "Transform";
                if (colliderAddedCount > 0)   return "Collider+";
                if (colliderRemovedCount > 0) return "Collider-";
                if (colliderEnabledCount > 0) return "Collider On";
                if (colliderDisabledCount > 0)return "Collider Off";
                if (goActivatedCount > 0)     return "Activated";
                if (goDeactivatedCount > 0)   return "Deactivated";
                if (goCreatedCount > 0)       return "Created";
                return "Destroyed";
            }
        }

        // ── Constants ─────────────────────────────────────────────────────────
        const float POSITION_THRESHOLD = 0.0001f;
        const float ROTATION_THRESHOLD = 0.0001f;
        const float SCALE_THRESHOLD    = 0.0001f;

        // ── State ─────────────────────────────────────────────────────────────
        static readonly Color k_Category = new Color(1.00f, 0.65f, 0.25f, 1f);

        Dictionary<GameObject, TransformSnapshot> m_TransformSnap;
        Dictionary<Collider,   ColliderSnapshot>  m_ColliderSnap;

        readonly List<GOEntry>                    m_Entries        = new();
        readonly Dictionary<GameObject, GOEntry>  m_LiveIndex      = new();
        readonly Dictionary<string,     GOEntry>  m_DestroyedIndex = new();

        readonly List<DiagnosticIssue>            m_Issues         = new();

        bool   m_IsCapturing;
        double m_CaptureTime;
        int    m_IterationCount;

        // Settings (exposed so the window can render them)
        public float Interval        { get; set; } = 0.5f;
        public bool  IsContinuous    { get; set; }
        public bool  LimitIterations { get; set; }
        public int   MaxIterations   { get; set; } = 10;
        public bool  IsCapturing     => m_IsCapturing;
        public int   IterationCount  => m_IterationCount;

        StaticRebuildDetector()
        {
            EditorApplication.update     += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                StopCapture();
        }

        // ── IDiagnosticDetector ───────────────────────────────────────────────
        public string Category      => "Static Rebuild";
        public Color  CategoryColor => k_Category;
        public bool   IsEnabled     { get; set; } = true;

        public IReadOnlyList<DiagnosticIssue> Issues => m_Issues;

        public event Action Changed;

        public void Clear()
        {
            m_Entries.Clear();
            m_LiveIndex.Clear();
            m_DestroyedIndex.Clear();
            m_Issues.Clear();
            m_IterationCount = 0;
            Changed?.Invoke();
        }

        // ── Capture control ───────────────────────────────────────────────────
        public void StartCapture()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Play Mode Required",
                    "Enter Play Mode before capturing — transforms must be live.", "OK");
                return;
            }
            TakeSnapshot();
            m_CaptureTime    = EditorApplication.timeSinceStartup;
            m_IsCapturing    = true;
            m_IterationCount = 0;
            Changed?.Invoke();
        }

        public void StopCapture()
        {
            m_IsCapturing = false;
            Changed?.Invoke();
        }

        void OnEditorUpdate()
        {
            if (!m_IsCapturing) return;

            if (!Application.isPlaying)
            {
                StopCapture();
                return;
            }

            if (EditorApplication.timeSinceStartup - m_CaptureTime < Interval) return;

            MergeResults(Compare());
            m_IterationCount++;
            RebuildIssues();
            Changed?.Invoke();

            if (IsContinuous && (!LimitIterations || m_IterationCount < MaxIterations))
            {
                TakeSnapshot();
                m_CaptureTime = EditorApplication.timeSinceStartup;
            }
            else
            {
                StopCapture();
            }
        }

        // ── Toolbar controls ──────────────────────────────────────────────────
        public void DrawToolbarControls()
        {
            if (m_IsCapturing && IsContinuous)
            {
                string label = LimitIterations
                    ? $"Stop ({m_IterationCount}/{MaxIterations})"
                    : $"Stop (iter {m_IterationCount})";
                if (GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(120)))
                    StopCapture();
            }
            else if (m_IsCapturing)
            {
                double elapsed = EditorApplication.timeSinceStartup - m_CaptureTime;
                GUILayout.Button($"…{elapsed:F1}s/{Interval:F1}s", EditorStyles.toolbarButton, GUILayout.Width(100));
            }
            else
            {
                string btnLabel = IsContinuous ? "▶ Start" : "Capture";
                if (GUILayout.Button(btnLabel, EditorStyles.toolbarButton, GUILayout.Width(70)))
                    StartCapture();
            }
        }

        // ── Settings popup ────────────────────────────────────────────────────
        public bool    HasSettings       => true;
        public Vector2 SettingsPopupSize => new Vector2(260f, 102f);

        public void DrawSettingsGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Interval (s):", EditorStyles.miniLabel, GUILayout.Width(80));
            float newInterval = EditorGUILayout.FloatField(Interval, GUILayout.Width(50));
            if (newInterval != Interval)
                Interval = Mathf.Clamp(newInterval, 0.05f, 5f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Loop:", EditorStyles.miniLabel, GUILayout.Width(80));
            IsContinuous = EditorGUILayout.Toggle(IsContinuous, GUILayout.Width(20));
            GUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!IsContinuous))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Limit iters:", EditorStyles.miniLabel, GUILayout.Width(80));
                LimitIterations = EditorGUILayout.Toggle(LimitIterations, GUILayout.Width(20));
                GUILayout.EndHorizontal();

                using (new EditorGUI.DisabledScope(!LimitIterations))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Max iters:", EditorStyles.miniLabel, GUILayout.Width(80));
                    int newMax = EditorGUILayout.IntField(MaxIterations, GUILayout.Width(50));
                    if (newMax != MaxIterations && newMax > 0) MaxIterations = newMax;
                    GUILayout.EndHorizontal();
                }
            }
        }

        // ── Details panel ─────────────────────────────────────────────────────
        public void DrawIssueDetails(DiagnosticIssue issue, ref Vector2 scroll, ref int traceIndex, ref Vector2 traceScroll)
        {
            if (issue?.DetectorPayload is not GOEntry entry) return;

            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Space(6);

            var prevColor = GUI.color;
            GUI.color = k_Category;
            GUILayout.Label("  Static Physics Rebuild", EditorStyles.boldLabel);
            GUI.color = prevColor;
            CanvasInvalidationDetector.DrawDivider();
            GUILayout.Space(4);

            CanvasInvalidationDetector.DrawSectionHeader("Rebuild Details");
            if (entry.transformCount > 0)
            {
                CanvasInvalidationDetector.DrawField("Transform", $"{entry.transformCount}×");
                if (entry.posCount > 0) CanvasInvalidationDetector.DrawField("  Position",   $"{entry.posCount}×");
                if (entry.rotCount > 0) CanvasInvalidationDetector.DrawField("  Rotation",   $"{entry.rotCount}×");
                if (entry.sclCount > 0) CanvasInvalidationDetector.DrawField("  Scale",      $"{entry.sclCount}×");
            }
            if (entry.colliderAddedCount   > 0) CanvasInvalidationDetector.DrawField("Collider Added",    $"{entry.colliderAddedCount}×");
            if (entry.colliderRemovedCount > 0) CanvasInvalidationDetector.DrawField("Collider Removed",  $"{entry.colliderRemovedCount}×");
            if (entry.colliderEnabledCount > 0) CanvasInvalidationDetector.DrawField("Collider Enabled",  $"{entry.colliderEnabledCount}×");
            if (entry.colliderDisabledCount> 0) CanvasInvalidationDetector.DrawField("Collider Disabled", $"{entry.colliderDisabledCount}×");
            if (entry.goActivatedCount     > 0) CanvasInvalidationDetector.DrawField("GO Activated",      $"{entry.goActivatedCount}×");
            if (entry.goDeactivatedCount   > 0) CanvasInvalidationDetector.DrawField("GO Deactivated",    $"{entry.goDeactivatedCount}×");
            if (entry.goCreatedCount       > 0) CanvasInvalidationDetector.DrawField("GO Created",        $"{entry.goCreatedCount}×");
            if (entry.goDestroyedCount     > 0) CanvasInvalidationDetector.DrawField("GO Destroyed",      $"{entry.goDestroyedCount}×");
            GUILayout.Space(6);

            CanvasInvalidationDetector.DrawSectionHeader("Capture Info");
            CanvasInvalidationDetector.DrawField("Total Events",  $"{entry.TotalCount}");
            CanvasInvalidationDetector.DrawField("Last Frame",    $"#{entry.FrameNumber}");
            CanvasInvalidationDetector.DrawField("Note", "No stack traces — snapshot-based detection");
            GUILayout.Space(6);

            GUILayout.EndScrollView();
        }

        // ── Issue list sync ───────────────────────────────────────────────────
        void RebuildIssues()
        {
            m_Issues.Clear();
            int id = 0;
            foreach (var entry in m_Entries)
            {
                m_Issues.Add(new DiagnosticIssue
                {
                    Id              = id++,
                    Count           = entry.TotalCount,
                    FrameNumber     = entry.FrameNumber,
                    Time            = 0f,
                    IsInPlayMode    = true,
                    Category        = Category,
                    CategoryColor   = k_Category,
                    IssueType       = entry.DominantType(),
                    IssueTypeColor  = k_Category,
                    ObjectName      = entry.goName,
                    HierarchyPath   = entry.go != null ? BuildPath(entry.go.transform) : entry.goName,
                    Target          = entry.go,
                    ContextName     = string.Empty,
                    DetectorPayload = entry,
                });
            }
        }

        // ── Accumulation ──────────────────────────────────────────────────────
        void MergeResults(List<(GameObject go, string name, ChangeReason reason, bool pos, bool rot, bool scl)> fresh)
        {
            foreach (var (go, name, reason, pos, rot, scl) in fresh)
            {
                var entry = FindOrCreateEntry(go, name);
                entry.FrameNumber = Time.frameCount;

                switch (reason)
                {
                    case ChangeReason.Transform:
                        entry.transformCount++;
                        if (pos) entry.posCount++;
                        if (rot) entry.rotCount++;
                        if (scl) entry.sclCount++;
                        break;
                    case ChangeReason.ColliderAdded:    entry.colliderAddedCount++;    break;
                    case ChangeReason.ColliderRemoved:  entry.colliderRemovedCount++;  break;
                    case ChangeReason.ColliderEnabled:  entry.colliderEnabledCount++;  break;
                    case ChangeReason.ColliderDisabled: entry.colliderDisabledCount++; break;
                    case ChangeReason.GOActivated:      entry.goActivatedCount++;      break;
                    case ChangeReason.GODeactivated:    entry.goDeactivatedCount++;    break;
                    case ChangeReason.GOCreated:        entry.goCreatedCount++;        break;
                    case ChangeReason.GODestroyed:      entry.goDestroyedCount++;      break;
                }
            }
        }

        GOEntry FindOrCreateEntry(GameObject go, string name)
        {
            if (go != null)
            {
                if (!m_LiveIndex.TryGetValue(go, out var e))
                {
                    e = new GOEntry { go = go, goName = name };
                    m_LiveIndex[go] = e;
                    m_Entries.Add(e);
                }
                return e;
            }
            else
            {
                if (!m_DestroyedIndex.TryGetValue(name, out var e))
                {
                    e = new GOEntry { go = null, goName = name };
                    m_DestroyedIndex[name] = e;
                    m_Entries.Add(e);
                }
                return e;
            }
        }

        // ── Snapshot ──────────────────────────────────────────────────────────
        void TakeSnapshot()
        {
            m_TransformSnap = new Dictionary<GameObject, TransformSnapshot>();
            m_ColliderSnap  = new Dictionary<Collider, ColliderSnapshot>();

            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (HasRigidbodyInSelfOrParents(go)) continue;
                if (!HasColliderInSelfOrChildren(go)) continue;

                var t = go.transform;
                m_TransformSnap[go] = new TransformSnapshot
                {
                    name              = go.name,
                    position          = t.position,
                    rotation          = t.rotation,
                    scale             = t.lossyScale,
                    activeInHierarchy = go.activeInHierarchy,
                };

                foreach (var c in go.GetComponents<Collider>())
                    m_ColliderSnap[c] = new ColliderSnapshot { go = go, goName = go.name, enabled = c.enabled };
            }
        }

        // ── Compare ───────────────────────────────────────────────────────────
        List<(GameObject go, string name, ChangeReason reason, bool pos, bool rot, bool scl)> Compare()
        {
            var results    = new List<(GameObject, string, ChangeReason, bool, bool, bool)>();
            var reportedGO = new HashSet<(int goId, ChangeReason reason)>();

            void Add(GameObject go, string name, ChangeReason reason,
                     bool pos = false, bool rot = false, bool scl = false)
            {
                int id = go != null ? go.GetHashCode() : name.GetHashCode() ^ (int)reason;
                if (!reportedGO.Add((id, reason))) return;
                results.Add((go, name, reason, pos, rot, scl));
            }

            var currentGOs = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var kvp in m_TransformSnap)
                if (kvp.Key == null) Add(null, kvp.Value.name, ChangeReason.GODestroyed);

            foreach (var go in currentGOs)
            {
                if (go == null) continue;
                if (HasRigidbodyInSelfOrParents(go)) continue;

                if (!m_TransformSnap.TryGetValue(go, out var snap))
                {
                    if (HasColliderInSelfOrChildren(go)) Add(go, go.name, ChangeReason.GOCreated);
                    continue;
                }

                if (go.activeInHierarchy != snap.activeInHierarchy)
                {
                    Add(go, go.name, go.activeInHierarchy ? ChangeReason.GOActivated : ChangeReason.GODeactivated);
                    continue;
                }

                if (!go.activeInHierarchy) continue;

                var t = go.transform;
                bool posMoved = (t.position   - snap.position).sqrMagnitude > POSITION_THRESHOLD * POSITION_THRESHOLD;
                bool rotMoved = Quaternion.Angle(snap.rotation, t.rotation)  > ROTATION_THRESHOLD;
                bool sclMoved = (t.lossyScale - snap.scale).sqrMagnitude     > SCALE_THRESHOLD   * SCALE_THRESHOLD;
                if (posMoved || rotMoved || sclMoved)
                    Add(go, go.name, ChangeReason.Transform, posMoved, rotMoved, sclMoved);

                foreach (var c in go.GetComponents<Collider>())
                {
                    if (m_ColliderSnap.TryGetValue(c, out var ce))
                    {
                        if (c.enabled != ce.enabled)
                            Add(go, go.name, c.enabled ? ChangeReason.ColliderEnabled : ChangeReason.ColliderDisabled);
                    }
                    else
                    {
                        Add(go, go.name, ChangeReason.ColliderAdded);
                    }
                }
            }

            foreach (var kvp in m_ColliderSnap)
                if (kvp.Key == null) Add(kvp.Value.go, kvp.Value.goName, ChangeReason.ColliderRemoved);

            return results;
        }

        // ── Queries ───────────────────────────────────────────────────────────
        static bool HasColliderInSelfOrChildren(GameObject go)
            => go.GetComponentInChildren<Collider>(true) != null;

        static bool HasRigidbodyInSelfOrParents(GameObject go)
            => go.GetComponentInParent<Rigidbody>(true) != null;

        static string BuildPath(Transform t)
        {
            var stack = new Stack<string>();
            var cur = t;
            while (cur != null) { stack.Push(cur.name); cur = cur.parent; }
            return string.Join("/", stack);
        }
    }
}
