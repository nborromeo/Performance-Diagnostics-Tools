using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class StaticRebuildAnalyzer : EditorWindow
{
    // -------------------------------------------------------------------------
    // Snapshot types
    // -------------------------------------------------------------------------

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
        public GameObject go;
        public string     goName;
        public bool       enabled;
    }

    private enum ChangeReason
    {
        Transform,
        ColliderAdded, ColliderRemoved, ColliderEnabled, ColliderDisabled,
        GOActivated, GODeactivated, GOCreated, GODestroyed,
    }

    // -------------------------------------------------------------------------
    // Accumulated data — one row per unique GO
    // -------------------------------------------------------------------------

    private class GOEntry
    {
        public GameObject go;       // null when destroyed
        public string     goName;

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
    }

    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const float POSITION_THRESHOLD = 0.0001f;
    private const float ROTATION_THRESHOLD = 0.0001f;
    private const float SCALE_THRESHOLD    = 0.0001f;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private Dictionary<GameObject, TransformSnapshot> _transformSnap;
    private Dictionary<Collider,   ColliderEntry>     _colliderSnap;

    private readonly List<GOEntry>                 _entries        = new();
    private readonly Dictionary<GameObject, GOEntry> _liveIndex    = new();
    private readonly Dictionary<string,     GOEntry> _destroyedIndex = new();

    private bool   _isCapturing;
    private double _captureTime;
    private float  _interval        = 0.5f;
    private bool   _continuous;
    private bool   _limitIterations;
    private int    _maxIterations   = 10;
    private int    _iterationCount;

    // UI references updated at runtime
    private MultiColumnListView _tableView;
    private Button              _captureBtn;
    private Label               _rowCountLabel;

    // -------------------------------------------------------------------------
    // Window
    // -------------------------------------------------------------------------

    [MenuItem("Window/Analysis/Static Rebuild Analyzer")]
    public static void ShowWindow() => GetWindow<StaticRebuildAnalyzer>("Static Rebuild Analyzer");

    private void OnEnable()  => EditorApplication.update += OnEditorUpdate;
    private void OnDisable() => EditorApplication.update -= OnEditorUpdate;

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.paddingTop    = 6;
        root.style.paddingLeft   = 6;
        root.style.paddingRight  = 6;
        root.style.paddingBottom = 6;

        root.Add(new HelpBox(
            "Detects static colliders (no Rigidbody in parent chain) that moved, toggled, " +
            "or had their GO created / destroyed / activated / deactivated. Click any row to select the object.",
            HelpBoxMessageType.Info));

        root.Add(MakeSettings());
        root.Add(MakeButtonRow());

        _rowCountLabel = new Label("No captures yet.");
        _rowCountLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
        _rowCountLabel.style.color          = new Color(0.6f, 0.6f, 0.6f);
        _rowCountLabel.style.marginBottom   = 4;
        root.Add(_rowCountLabel);

        _tableView = BuildTable();
        root.Add(_tableView);
    }

    // -------------------------------------------------------------------------
    // Settings panel
    // -------------------------------------------------------------------------

    private VisualElement MakeSettings()
    {
        var box = new VisualElement();
        box.style.marginTop    = 4;
        box.style.marginBottom = 4;

        var intervalSlider = new Slider("Interval (s)", 0.05f, 5f) { value = _interval, showInputField = true };
        intervalSlider.RegisterValueChangedCallback(e => _interval = e.newValue);
        box.Add(intervalSlider);

        var continuousToggle = new Toggle("Continuous") { value = _continuous };

        var limitToggle = new Toggle("Limit Iterations") { value = _limitIterations };
        limitToggle.SetEnabled(_continuous);

        var maxIterSlider = new SliderInt("Max Iterations", 1, 1000) { value = _maxIterations, showInputField = true };
        maxIterSlider.SetEnabled(_continuous && _limitIterations);

        continuousToggle.RegisterValueChangedCallback(e =>
        {
            _continuous = e.newValue;
            limitToggle.SetEnabled(_continuous);
            maxIterSlider.SetEnabled(_continuous && _limitIterations);
            RefreshCaptureButton();
        });
        limitToggle.RegisterValueChangedCallback(e =>
        {
            _limitIterations = e.newValue;
            maxIterSlider.SetEnabled(_continuous && _limitIterations);
        });
        maxIterSlider.RegisterValueChangedCallback(e => _maxIterations = e.newValue);

        box.Add(continuousToggle);
        box.Add(limitToggle);
        box.Add(maxIterSlider);
        return box;
    }

    private VisualElement MakeButtonRow()
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.marginBottom  = 4;

        _captureBtn = new Button(OnCaptureClicked) { text = "Capture" };
        _captureBtn.style.flexGrow = 1;
        _captureBtn.style.height   = 28;

        var clearBtn = new Button(ClearEntries) { text = "Clear" };
        clearBtn.style.width  = 60;
        clearBtn.style.height = 28;

        row.Add(_captureBtn);
        row.Add(clearBtn);
        return row;
    }

    // -------------------------------------------------------------------------
    // Table
    // -------------------------------------------------------------------------

    private MultiColumnListView BuildTable()
    {
        var lv = new MultiColumnListView
        {
            itemsSource                   = _entries,
            showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
            selectionType                 = SelectionType.Single,
            style                         = { flexGrow = 1 },
        };

        lv.selectionChanged += selected =>
        {
            if (selected.FirstOrDefault() is GOEntry entry && entry.go != null)
            {
                Selection.activeGameObject = entry.go;
                EditorGUIUtility.PingObject(entry.go);
            }
        };

        // Name column
        lv.columns.reorderable = true;
        lv.sortingEnabled = true;
        lv.columnSortingChanged += () => ApplySort(_tableView);

        lv.columns.Add(new Column
        {
            name        = "name",
            title       = "Object",
            sortable    = true,
            width       = 180,
            stretchable = true,
            makeHeader  = () => MakeHeaderLabel("Object", "GameObject name. Grey = destroyed, no longer selectable."),
            makeCell    = () => new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, paddingLeft = 4 } },
            bindCell    = (e, i) =>
            {
                var entry = _entries[i];
                var lbl   = (Label)e;
                lbl.text        = entry.goName;
                lbl.style.color = entry.go == null
                    ? new Color(0.55f, 0.55f, 0.55f)
                    : new Color(0.9f,  0.9f,  0.9f);
            },
        });

        AddCountColumn(lv, "move",  "Move",  "Transform changed\nHow many captures detected this GO moving, rotating, or scaling.", e => e.transformCount);
        AddCountColumn(lv, "c_add", "C+",    "Collider Added\nA Collider component was added to this GO.",                         e => e.colliderAddedCount);
        AddCountColumn(lv, "c_rem", "C-",    "Collider Removed\nA Collider component was destroyed on this GO.",                   e => e.colliderRemovedCount);
        AddCountColumn(lv, "c_on",  "C▲",    "Collider Enabled\nA Collider component was switched on (enabled = true).",           e => e.colliderEnabledCount);
        AddCountColumn(lv, "c_off", "C▼",    "Collider Disabled\nA Collider component was switched off (enabled = false).",        e => e.colliderDisabledCount);
        AddCountColumn(lv, "act",   "Act",   "GO Activated\nThis GO's activeInHierarchy flipped from false to true.",              e => e.goActivatedCount);
        AddCountColumn(lv, "deact", "Deact", "GO Deactivated\nThis GO's activeInHierarchy flipped from true to false.",            e => e.goDeactivatedCount);
        AddCountColumn(lv, "born",  "Born",  "GO Created\nThis GO did not exist in the previous snapshot.",                       e => e.goCreatedCount);
        AddCountColumn(lv, "dead",  "Dead",  "GO Destroyed\nThis GO was present in the previous snapshot but is now gone.",       e => e.goDestroyedCount);

        return lv;
    }

    private void AddCountColumn(MultiColumnListView lv, string name, string title, string tooltip, System.Func<GOEntry, int> getValue)
    {
        lv.columns.Add(new Column
        {
            name       = name,
            title      = title,
            width      = 46,
            sortable   = true,
            makeHeader = () => MakeHeaderLabel(title, tooltip),
            makeCell   = () =>
            {
                var lbl = new Label { tooltip = tooltip };
                lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
                return lbl;
            },
            bindCell = (e, i) =>
            {
                int count = getValue(_entries[i]);
                var lbl   = (Label)e;
                lbl.text        = count > 0 ? count.ToString() : string.Empty;
                lbl.style.color = count > 0 ? Color.white : new Color(0.3f, 0.3f, 0.3f);
            },
        });
    }

    private void ApplySort(MultiColumnListView lv)
    {
        var descs = lv.sortedColumns.ToList();
        if (descs.Count == 0) return;

        var desc = descs[0];
        bool asc = desc.direction == SortDirection.Ascending;

        _entries.Sort((a, b) =>
        {
            int cmp = desc.columnName switch
            {
                "name"   => string.Compare(a.goName, b.goName, System.StringComparison.OrdinalIgnoreCase),
                "move"   => a.transformCount.CompareTo(b.transformCount),
                "c_add"  => a.colliderAddedCount.CompareTo(b.colliderAddedCount),
                "c_rem"  => a.colliderRemovedCount.CompareTo(b.colliderRemovedCount),
                "c_on"   => a.colliderEnabledCount.CompareTo(b.colliderEnabledCount),
                "c_off"  => a.colliderDisabledCount.CompareTo(b.colliderDisabledCount),
                "act"    => a.goActivatedCount.CompareTo(b.goActivatedCount),
                "deact"  => a.goDeactivatedCount.CompareTo(b.goDeactivatedCount),
                "born"   => a.goCreatedCount.CompareTo(b.goCreatedCount),
                "dead"   => a.goDestroyedCount.CompareTo(b.goDestroyedCount),
                _        => 0,
            };
            return asc ? cmp : -cmp;
        });

        lv.RefreshItems();
    }

    private static Label MakeHeaderLabel(string title, string tooltip)
    {
        var lbl = new Label(title)
        {
            tooltip = tooltip,
            style   = { unityTextAlign = TextAnchor.MiddleCenter, flexGrow = 1 },
        };
        return lbl;
    }

    // -------------------------------------------------------------------------
    // Capture flow
    // -------------------------------------------------------------------------

    private void OnCaptureClicked()
    {
        if (_isCapturing && _continuous)
        {
            _isCapturing = false;
            RefreshCaptureButton();
        }
        else
        {
            StartCapture();
        }
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
        _captureTime    = EditorApplication.timeSinceStartup;
        _isCapturing    = true;
        _iterationCount = 0;
        RefreshCaptureButton();
    }

    private void OnEditorUpdate()
    {
        if (!_isCapturing) return;

        if (!Application.isPlaying)
        {
            _isCapturing = false;
            RefreshCaptureButton();
            return;
        }

        RefreshCaptureButton(); // update elapsed time label while waiting

        if (EditorApplication.timeSinceStartup - _captureTime < _interval) return;

        MergeResults(Compare());
        _iterationCount++;
        RefreshRowCount();
        _tableView?.RefreshItems();

        if (_continuous && (!_limitIterations || _iterationCount < _maxIterations))
        {
            TakeSnapshot();
            _captureTime = EditorApplication.timeSinceStartup;
        }
        else
        {
            _isCapturing = false;
            RefreshCaptureButton();
        }
    }

    private void RefreshCaptureButton()
    {
        if (_captureBtn == null) return;

        if (_isCapturing && _continuous)
        {
            _captureBtn.text = _limitIterations
                ? $"Stop  ({_iterationCount} / {_maxIterations})"
                : $"Stop  (iter {_iterationCount})";
        }
        else if (_isCapturing)
        {
            double elapsed = EditorApplication.timeSinceStartup - _captureTime;
            _captureBtn.text = $"Capturing…  {elapsed:F2}s / {_interval:F2}s";
        }
        else
        {
            _captureBtn.text = _continuous ? "Start" : "Capture";
        }
    }

    private void RefreshRowCount()
    {
        if (_rowCountLabel == null) return;
        _rowCountLabel.text = _entries.Count == 0
            ? "No issues found."
            : $"{_entries.Count} unique GO(s) with issues  —  {_iterationCount} capture(s) so far";
    }

    private void ClearEntries()
    {
        _entries.Clear();
        _liveIndex.Clear();
        _destroyedIndex.Clear();
        _tableView?.RefreshItems();
        RefreshRowCount();
    }

    // -------------------------------------------------------------------------
    // Accumulation — merge raw detections into per-GO rows
    // -------------------------------------------------------------------------

    private void MergeResults(List<(GameObject go, string name, ChangeReason reason, bool pos, bool rot, bool scl)> fresh)
    {
        foreach (var (go, name, reason, pos, rot, scl) in fresh)
        {
            GOEntry entry = FindOrCreateEntry(go, name);

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

    private GOEntry FindOrCreateEntry(GameObject go, string name)
    {
        if (go != null)
        {
            if (!_liveIndex.TryGetValue(go, out var e))
            {
                e = new GOEntry { go = go, goName = name };
                _liveIndex[go] = e;
                _entries.Add(e);
            }
            return e;
        }
        else
        {
            if (!_destroyedIndex.TryGetValue(name, out var e))
            {
                e = new GOEntry { go = null, goName = name };
                _destroyedIndex[name] = e;
                _entries.Add(e);
            }
            return e;
        }
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

    private List<(GameObject go, string name, ChangeReason reason, bool pos, bool rot, bool scl)> Compare()
    {
        var results    = new List<(GameObject, string, ChangeReason, bool, bool, bool)>();
        var reportedGO = new HashSet<(int goId, ChangeReason reason)>();

        void Add(GameObject go, string name, ChangeReason reason, bool pos = false, bool rot = false, bool scl = false)
        {
            int id = go != null ? go.GetHashCode() : name.GetHashCode() ^ (int)reason;
            if (!reportedGO.Add((id, reason))) return;
            results.Add((go, name, reason, pos, rot, scl));
        }

        var currentGOs = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // Destroyed GOs
        foreach (var kvp in _transformSnap)
            if (kvp.Key == null) Add(null, kvp.Value.name, ChangeReason.GODestroyed);

        foreach (var go in currentGOs)
        {
            if (go == null) continue;
            if (HasRigidbodyInSelfOrParents(go)) continue;

            if (!_transformSnap.TryGetValue(go, out var snap))
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

        foreach (var kvp in _colliderSnap)
            if (kvp.Key == null) Add(kvp.Value.go, kvp.Value.goName, ChangeReason.ColliderRemoved);

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
