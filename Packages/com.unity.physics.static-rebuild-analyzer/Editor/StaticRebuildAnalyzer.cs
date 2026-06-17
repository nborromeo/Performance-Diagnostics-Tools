using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class StaticRebuildAnalyzer : EditorWindow
{
    private struct TransformSnapshot
    {
        public string     name;
        public Vector3    position;
        public Quaternion rotation;
        public Vector3    scale;
        public bool       activeInHierarchy;
    }

    private struct ColliderEntry
    {
        public GameObject go;   // stored here because after destruction c.gameObject throws
        public string     goName;
        public bool       enabled;
    }

    private enum ChangeReason
    {
        Transform,
        ColliderAdded, ColliderRemoved, ColliderEnabled, ColliderDisabled,
        GOActivated, GODeactivated, GOCreated, GODestroyed,
    }

    private struct Result
    {
        public GameObject go;       // null when GO was destroyed
        public string     goName;   // always valid
        public ChangeReason reason;
        public bool positionChanged;
        public bool rotationChanged;
        public bool scaleChanged;
    }

    private const float POSITION_THRESHOLD = 0.0001f;
    private const float ROTATION_THRESHOLD = 0.0001f;
    private const float SCALE_THRESHOLD    = 0.0001f;

    private Dictionary<GameObject, TransformSnapshot> _transformSnap;
    private Dictionary<Collider,   ColliderEntry>     _colliderSnap;

    private List<Result> _results    = new();
    private Vector2      _scrollPos;
    private bool         _isCapturing;
    private double       _captureTime;
    private float        _interval   = 0.5f;
    private bool         _continuous;

    [MenuItem("Window/Analysis/Static Rebuild Analyzer")]
    public static void ShowWindow() => GetWindow<StaticRebuildAnalyzer>("Static Rebuild Analyzer");

    private void OnEnable()  => EditorApplication.update += OnEditorUpdate;
    private void OnDisable() => EditorApplication.update -= OnEditorUpdate;

    private void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Static Rebuild Analyzer", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Reports static colliders (no Rigidbody in parent chain) that moved, had a collider " +
            "added/removed/toggled, or had their GameObject created/destroyed/activated/deactivated. " +
            "All trigger a broadphase rebuild.",
            MessageType.Info);

        EditorGUILayout.Space(4);
        _interval   = EditorGUILayout.Slider("Interval (seconds)", _interval, 0.05f, 5f);
        _continuous = EditorGUILayout.Toggle("Continuous", _continuous);

        if (_continuous && _isCapturing)
        {
            if (GUILayout.Button("Stop", GUILayout.Height(30)))
            {
                _isCapturing = false;
                Repaint();
            }
        }
        else
        {
            EditorGUI.BeginDisabledGroup(_isCapturing);
            string label = _isCapturing
                ? $"Capturing… ({EditorApplication.timeSinceStartup - _captureTime:F2}s / {_interval:F2}s)"
                : (_continuous ? "Start" : "Capture");
            if (GUILayout.Button(label, GUILayout.Height(30)))
                StartCapture();
            EditorGUI.EndDisabledGroup();
        }

        if (_results.Count > 0)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"Results: {_results.Count} issue(s) found", EditorStyles.boldLabel);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            foreach (var r in _results)
                DrawResult(r);
            EditorGUILayout.EndScrollView();
        }
        else if (!_isCapturing && _transformSnap != null)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("No issues found.", EditorStyles.centeredGreyMiniLabel);
        }
    }

    private static readonly Color _rowColor = new Color(0.5f, 0.1f, 0.1f, 0.3f);

    private void DrawResult(Result r)
    {
        var rect = EditorGUILayout.BeginVertical();
        EditorGUI.DrawRect(rect, _rowColor);

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(r.go == null);
        if (GUILayout.Button("Select", GUILayout.Width(55)) && r.go != null)
        {
            Selection.activeGameObject = r.go;
            EditorGUIUtility.PingObject(r.go);
        }
        EditorGUI.EndDisabledGroup();
        GUILayout.Label(r.goName, EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(60);
        switch (r.reason)
        {
            case ChangeReason.Transform:
                if (r.positionChanged) GUILayout.Label("Position", EditorStyles.miniLabel, GUILayout.Width(60));
                if (r.rotationChanged) GUILayout.Label("Rotation", EditorStyles.miniLabel, GUILayout.Width(60));
                if (r.scaleChanged)    GUILayout.Label("Scale",    EditorStyles.miniLabel, GUILayout.Width(50));
                break;
            case ChangeReason.ColliderAdded:    GUILayout.Label("Collider Added",    EditorStyles.miniLabel); break;
            case ChangeReason.ColliderRemoved:  GUILayout.Label("Collider Removed",  EditorStyles.miniLabel); break;
            case ChangeReason.ColliderEnabled:  GUILayout.Label("Collider Enabled",  EditorStyles.miniLabel); break;
            case ChangeReason.ColliderDisabled: GUILayout.Label("Collider Disabled", EditorStyles.miniLabel); break;
            case ChangeReason.GOActivated:      GUILayout.Label("GO Activated",      EditorStyles.miniLabel); break;
            case ChangeReason.GODeactivated:    GUILayout.Label("GO Deactivated",    EditorStyles.miniLabel); break;
            case ChangeReason.GOCreated:        GUILayout.Label("GO Created",        EditorStyles.miniLabel); break;
            case ChangeReason.GODestroyed:      GUILayout.Label("GO Destroyed",      EditorStyles.miniLabel); break;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    private void StartCapture()
    {
        if (!Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Play Mode Required",
                "Enter Play Mode before capturing — transforms must be live.", "OK");
            return;
        }

        TakeSnapshot();
        _captureTime = EditorApplication.timeSinceStartup;
        _isCapturing = true;
        _results.Clear();
        Repaint();
    }

    private void OnEditorUpdate()
    {
        if (!_isCapturing) return;

        if (!Application.isPlaying)
        {
            _isCapturing = false;
            Repaint();
            return;
        }

        if (EditorApplication.timeSinceStartup - _captureTime < _interval) return;

        _results = Compare();
        if (_continuous)
        {
            TakeSnapshot();
            _captureTime = EditorApplication.timeSinceStartup;
        }
        else
        {
            _isCapturing = false;
        }
        Repaint();
    }

    // -------------------------------------------------------------------------
    // Snapshot
    // -------------------------------------------------------------------------

    private void TakeSnapshot()
    {
        _transformSnap = new Dictionary<GameObject, TransformSnapshot>();
        _colliderSnap  = new Dictionary<Collider, ColliderEntry>();

        foreach (var go in FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (HasRigidbodyInSelfOrParents(go)) continue;
            if (!HasColliderInSelfOrChildren(go)) continue;

            var t = go.transform;
            _transformSnap[go] = new TransformSnapshot
            {
                name              = go.name,
                position          = t.position,
                rotation          = t.rotation,
                scale             = t.lossyScale,
                activeInHierarchy = go.activeInHierarchy,
            };

            foreach (var c in go.GetComponents<Collider>())
                _colliderSnap[c] = new ColliderEntry { go = go, goName = go.name, enabled = c.enabled };
        }
    }

    // -------------------------------------------------------------------------
    // Compare
    // -------------------------------------------------------------------------

    private List<Result> Compare()
    {
        var results    = new List<Result>();
        var reportedGO = new HashSet<(int goId, ChangeReason reason)>();

        void Add(GameObject go, string name, ChangeReason reason, bool pos = false, bool rot = false, bool scl = false)
        {
            int id = go != null ? go.GetHashCode() : name.GetHashCode() ^ (int)reason;
            if (!reportedGO.Add((id, reason))) return;
            results.Add(new Result { go = go, goName = name, reason = reason,
                positionChanged = pos, rotationChanged = rot, scaleChanged = scl });
        }

        var currentGOs = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // --- Destroyed GOs: in old snapshot but gone now ---
        foreach (var kvp in _transformSnap)
        {
            if (kvp.Key == null)
                Add(null, kvp.Value.name, ChangeReason.GODestroyed);
        }

        foreach (var go in currentGOs)
        {
            if (go == null) continue;
            if (HasRigidbodyInSelfOrParents(go)) continue;

            if (!_transformSnap.TryGetValue(go, out var snap))
            {
                // New GO that didn't exist in the previous snapshot
                if (HasColliderInSelfOrChildren(go))
                    Add(go, go.name, ChangeReason.GOCreated);
                continue;
            }

            // GO activated / deactivated
            if (go.activeInHierarchy != snap.activeInHierarchy)
            {
                Add(go, go.name, go.activeInHierarchy ? ChangeReason.GOActivated : ChangeReason.GODeactivated);
                continue; // active state change already covers collider visibility changes
            }

            if (!go.activeInHierarchy) continue;

            // Transform
            var t = go.transform;
            bool posMoved = (t.position   - snap.position).sqrMagnitude > POSITION_THRESHOLD * POSITION_THRESHOLD;
            bool rotMoved = Quaternion.Angle(snap.rotation, t.rotation)  > ROTATION_THRESHOLD;
            bool sclMoved = (t.lossyScale - snap.scale).sqrMagnitude     > SCALE_THRESHOLD   * SCALE_THRESHOLD;
            if (posMoved || rotMoved || sclMoved)
                Add(go, go.name, ChangeReason.Transform, posMoved, rotMoved, sclMoved);

            // Collider enable/disable and newly added components
            foreach (var c in go.GetComponents<Collider>())
            {
                if (_colliderSnap.TryGetValue(c, out var ce))
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

        // --- Destroyed collider components (component was removed but GO may still exist) ---
        foreach (var kvp in _colliderSnap)
        {
            if (kvp.Key == null)
                Add(kvp.Value.go, kvp.Value.goName, ChangeReason.ColliderRemoved);
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------

    private static bool HasColliderInSelfOrChildren(GameObject go)
        => go.GetComponentInChildren<Collider>(true) != null;

    private static bool HasRigidbodyInSelfOrParents(GameObject go)
        => go.GetComponentInParent<Rigidbody>(true) != null;
}
