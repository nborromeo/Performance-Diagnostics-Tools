using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace NativeProfiler
{
    internal sealed class NativeProfilerWindow : EditorWindow
    {
        enum TargetMode { ThisEditor, OtherProcess }

        [SerializeField] TargetMode m_TargetMode;
        [SerializeField] string m_OtherTarget = "";
        [SerializeField] float m_Duration = 5f;
        [SerializeField] bool m_Inverted;
        [SerializeField] bool m_ManagedOnly;
        [SerializeField] int m_ThreadSelection = -2; // -2 = pick main thread on load, -1 = all
        [SerializeField] TreeViewState<int> m_TreeState;
        [SerializeField] MultiColumnHeaderState m_HeaderState;

        CallTreeView m_TreeView;
        SearchField m_SearchField;
        ProfileData m_ShownData;
        bool m_TreeDirty;

        // Timeline selection, in ns relative to trace start (max <= 0 = none).
        long m_SelMin, m_SelMax;
        float m_DragStartX = -1;
        float[] m_Bins;
        int m_BinsThread = int.MinValue;

        [MenuItem("Window/Analysis/Native CPU Profiler (xctrace)")]
        static void Open() => GetWindow<NativeProfilerWindow>("Native CPU Profiler").Show();

        void OnEnable()
        {
            m_TreeState ??= new TreeViewState<int>();
            var header = CallTreeView.CreateHeaderState();
            if (MultiColumnHeaderState.CanOverwriteSerializedFields(m_HeaderState, header))
                MultiColumnHeaderState.OverwriteSerializedFields(m_HeaderState, header);
            m_HeaderState = header;
            m_TreeView = new CallTreeView(m_TreeState, new CallTreeView.Header(header)) { FunctionDoubleClicked = OpenSource };
            m_SearchField = new SearchField();
            m_SearchField.downOrUpArrowKeyPressed += m_TreeView.SetFocusAndEnsureSelectedItem;
            CaptureSession.Changed += OnSessionChanged;
            m_TreeDirty = true;
        }

        void OnDisable() => CaptureSession.Changed -= OnSessionChanged;

        void OnSessionChanged()
        {
            m_TreeDirty = true;
            Repaint();
        }

        void OnInspectorUpdate()
        {
            if (CaptureSession.IsBusy)
                Repaint();
        }

        void OnGUI()
        {
            if (!CaptureSession.IsSupported)
            {
                EditorGUILayout.HelpBox("This tool drives Apple's xctrace and only runs on the macOS Editor.", MessageType.Info);
                return;
            }

            DrawRecordToolbar();
            DrawStatus();

            var data = CaptureSession.Data;
            if (data == null)
                return;
            if (!ReferenceEquals(data, m_ShownData))
            {
                m_ShownData = data;
                m_SelMin = m_SelMax = 0;
                m_Bins = null;
                if (m_ThreadSelection == -2 || m_ThreadSelection >= data.Threads.Count)
                    m_ThreadSelection = data.Threads.FindIndex(t => t.IsMain);
                m_TreeDirty = true;
            }

            DrawViewToolbar(data);
            DrawTimeline(data);
            if (m_TreeDirty)
                RebuildTree(data);

            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            m_TreeView.Draw(rect);
            DrawFooter(data);
        }

        // -------------------------------------------------------------------------------------------
        // Toolbars

        void DrawRecordToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var busy = CaptureSession.IsBusy;
                using (new EditorGUI.DisabledScope(busy))
                {
                    m_TargetMode = (TargetMode)EditorGUILayout.EnumPopup(m_TargetMode, EditorStyles.toolbarDropDown, GUILayout.Width(100));
                    if (m_TargetMode == TargetMode.OtherProcess)
                    {
                        m_OtherTarget = EditorGUILayout.TextField(m_OtherTarget, EditorStyles.toolbarTextField, GUILayout.Width(140));
                        if (EditorGUILayout.DropdownButton(new GUIContent("Pick"), FocusType.Passive, EditorStyles.toolbarDropDown, GUILayout.Width(45)))
                            ShowProcessMenu();
                    }
                    GUILayout.Label("Duration", EditorStyles.miniLabel, GUILayout.Width(48));
                    m_Duration = Mathf.Max(0, EditorGUILayout.FloatField(m_Duration, EditorStyles.toolbarTextField, GUILayout.Width(35)));
                    GUILayout.Label(m_Duration > 0 ? "s" : "s (until Stop)", EditorStyles.miniLabel);
                }

                if (CaptureSession.Phase == CapturePhase.Recording)
                {
                    if (GUILayout.Button("■ Stop", EditorStyles.toolbarButton, GUILayout.Width(60)))
                        CaptureSession.StopRecording();
                }
                else
                {
                    using (new EditorGUI.DisabledScope(busy || (m_TargetMode == TargetMode.OtherProcess && string.IsNullOrWhiteSpace(m_OtherTarget))))
                    {
                        if (GUILayout.Button("● Record", EditorStyles.toolbarButton, GUILayout.Width(70)))
                        {
                            var target = m_TargetMode == TargetMode.ThisEditor
                                ? System.Diagnostics.Process.GetCurrentProcess().Id.ToString()
                                : m_OtherTarget.Trim();
                            CaptureSession.StartRecording(target, m_Duration);
                        }
                    }
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(busy))
                {
                    if (EditorGUILayout.DropdownButton(new GUIContent("Captures"), FocusType.Passive, EditorStyles.toolbarDropDown, GUILayout.Width(75)))
                        ShowCapturesMenu();
                    if (GUILayout.Button("Import .trace…", EditorStyles.toolbarButton))
                    {
                        var path = EditorUtility.OpenFilePanel("Import Instruments trace", "", "trace");
                        if (!string.IsNullOrEmpty(path))
                            CaptureSession.ImportTrace(path);
                    }
                }

                var data = CaptureSession.Data;
                using (new EditorGUI.DisabledScope(data == null))
                {
                    if (GUILayout.Button("Open in Instruments", EditorStyles.toolbarButton))
                        EditorUtility.OpenWithDefaultApp(data.TracePath);
                    if (GUILayout.Button("Export Folded…", EditorStyles.toolbarButton))
                        ExportFolded(data);
                }
            }
        }

        void DrawStatus()
        {
            switch (CaptureSession.Phase)
            {
                case CapturePhase.Recording:
                    var elapsed = (DateTime.UtcNow - CaptureSession.RecordStartUtc).TotalSeconds;
                    var limit = m_Duration > 0 ? $" / {m_Duration:0.#}s" : "";
                    EditorGUILayout.HelpBox($"Recording with xctrace… {elapsed:0.0}s{limit}. Domain reloads (e.g. entering Play Mode) are fine, the capture keeps going.", MessageType.None);
                    break;
                case CapturePhase.Exporting:
                    EditorGUILayout.HelpBox("Exporting samples from the trace (xctrace export)…", MessageType.None);
                    break;
                case CapturePhase.Parsing:
                    EditorGUILayout.HelpBox("Parsing samples and resolving managed frames…", MessageType.None);
                    break;
                case CapturePhase.Error:
                    EditorGUILayout.HelpBox(CaptureSession.Error, MessageType.Error);
                    break;
                case CapturePhase.Idle:
                    EditorGUILayout.HelpBox(
                        "Samples a process with Instruments' Time Profiler (xctrace) and shows native + managed call stacks.\n" +
                        "• This Editor: Mono JIT frames are resolved to C# methods using the Editor's own Mono runtime.\n" +
                        "• Other process: IL2CPP players are fully symbolicated by xctrace; Mono players show JIT frames as addresses.\n" +
                        "Tip: keep the Game view focused / Play Mode running while recording, an unfocused Editor throttles its update loop.",
                        MessageType.Info);
                    break;
            }
        }

        void DrawViewToolbar(ProfileData data)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                var inverted = GUILayout.Toolbar(m_Inverted ? 1 : 0, new[] { "Top-Down", "Bottom-Up" }, EditorStyles.toolbarButton, GUILayout.Width(150)) == 1;

                var threadNames = new List<string> { "All threads" };
                var order = Enumerable.Range(0, data.Threads.Count).OrderByDescending(i => data.Threads[i].TotalWeightNs).ToList();
                threadNames.AddRange(order.Select(i => $"{data.Threads[i].Name}  ({data.Threads[i].TotalWeightNs / 1e6:0} ms)"));
                var popupIndex = m_ThreadSelection < 0 ? 0 : order.IndexOf(m_ThreadSelection) + 1;
                popupIndex = EditorGUILayout.Popup(popupIndex, threadNames.ToArray(), EditorStyles.toolbarPopup, GUILayout.Width(280));
                var thread = popupIndex <= 0 ? -1 : order[popupIndex - 1];

                var managedOnly = GUILayout.Toggle(m_ManagedOnly, new GUIContent("Managed only", "Hide native frames, keeping the first native callee under managed code."), EditorStyles.toolbarButton, GUILayout.Width(95));
                if (EditorGUI.EndChangeCheck())
                {
                    m_Inverted = inverted;
                    m_ThreadSelection = thread;
                    m_ManagedOnly = managedOnly;
                    m_Bins = null;
                    m_TreeDirty = true;
                }

                if (GUILayout.Button("Expand Heaviest", EditorStyles.toolbarButton, GUILayout.Width(100)))
                    m_TreeView.ExpandHeaviestPath();
                if (GUILayout.Button("Collapse", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    m_TreeView.CollapseAll();

                GUILayout.FlexibleSpace();
                m_TreeView.searchString = m_SearchField.OnToolbarGUI(m_TreeView.searchString, GUILayout.MinWidth(150), GUILayout.MaxWidth(300));
            }
        }

        // -------------------------------------------------------------------------------------------
        // Timeline: per-bin CPU time of the selected thread(s); drag to restrict the call tree to a range.

        void DrawTimeline(ProfileData data)
        {
            var rect = GUILayoutUtility.GetRect(0, 10000, 46, 46);
            rect.xMin += 2;
            rect.xMax -= 2;
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.13f, 0.13f, 0.13f) : new Color(0.8f, 0.8f, 0.8f));

            var duration = Math.Max(1, data.MaxTimeNs);
            var binCount = Mathf.Clamp((int)rect.width / 2, 10, 1000);
            if (m_Bins == null || m_Bins.Length != binCount || m_BinsThread != m_ThreadSelection)
            {
                m_Bins = new float[binCount];
                m_BinsThread = m_ThreadSelection;
                var binNs = duration / (double)binCount;
                foreach (var s in data.Samples)
                {
                    if (m_ThreadSelection >= 0 && s.Thread != m_ThreadSelection)
                        continue;
                    var b = Mathf.Clamp((int)(s.TimeNs / binNs), 0, binCount - 1);
                    m_Bins[b] += s.WeightNs;
                }
                var max = Mathf.Max(1, m_Bins.Max());
                for (var i = 0; i < binCount; i++)
                    m_Bins[i] /= max;
            }

            var barColor = new Color(0.3f, 0.65f, 1f, 0.9f);
            var w = rect.width / binCount;
            for (var i = 0; i < binCount; i++)
            {
                var h = m_Bins[i] * (rect.height - 2);
                if (h > 0)
                    EditorGUI.DrawRect(new Rect(rect.x + i * w, rect.yMax - h, Mathf.Max(1, w - 0.5f), h), barColor);
            }

            if (m_SelMax > 0)
            {
                var x0 = rect.x + rect.width * (m_SelMin / (float)duration);
                var x1 = rect.x + rect.width * (m_SelMax / (float)duration);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, x0 - rect.x, rect.height), new Color(0, 0, 0, 0.45f));
                EditorGUI.DrawRect(new Rect(x1, rect.y, rect.xMax - x1, rect.height), new Color(0, 0, 0, 0.45f));
            }

            var label = m_SelMax > 0
                ? $"{m_SelMin / 1e9:0.000}s – {m_SelMax / 1e9:0.000}s  (double-click to clear)"
                : $"{duration / 1e9:0.00}s — drag to select a range";
            GUI.Label(new Rect(rect.x + 4, rect.y + 1, rect.width, 16), label, EditorStyles.miniLabel);

            var e = Event.current;
            float ToNs(float x) => Mathf.Clamp01((x - rect.x) / rect.width) * duration;
            switch (e.type)
            {
                case EventType.MouseDown when rect.Contains(e.mousePosition) && e.button == 0:
                    if (e.clickCount == 2)
                    {
                        m_SelMin = m_SelMax = 0;
                        m_TreeDirty = true;
                    }
                    else
                        m_DragStartX = e.mousePosition.x;
                    e.Use();
                    break;
                case EventType.MouseDrag when m_DragStartX >= 0:
                    m_SelMin = (long)ToNs(Mathf.Min(m_DragStartX, e.mousePosition.x));
                    m_SelMax = (long)ToNs(Mathf.Max(m_DragStartX, e.mousePosition.x));
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when m_DragStartX >= 0:
                    m_DragStartX = -1;
                    if (m_SelMax - m_SelMin < duration / 500)
                        m_SelMin = m_SelMax = 0;
                    m_TreeDirty = true;
                    e.Use();
                    break;
            }
        }

        void RebuildTree(ProfileData data)
        {
            m_TreeDirty = false;
            var tree = CallTree.Build(data, CurrentOptions());
            m_TreeView.SetTree(data, tree, expandHeaviest: !m_Inverted);
        }

        CallTreeOptions CurrentOptions() => new CallTreeOptions
        {
            Inverted = m_Inverted,
            ManagedOnly = m_ManagedOnly,
            Thread = m_ThreadSelection,
            MinTimeNs = m_SelMax > 0 ? m_SelMin : 0,
            MaxTimeNs = m_SelMax > 0 ? m_SelMax : 0,
        };

        void DrawFooter(ProfileData data)
        {
            var tree = m_TreeView.Tree;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var resolved = data.JitFramesTotal > 0 ? $"  |  managed frames resolved: {data.JitFramesResolved}/{data.JitFramesTotal}" : "";
                GUILayout.Label($"{data.TargetName} ({data.TargetPid})  |  {tree?.SampleCount ?? 0} samples, {(tree?.TotalNs ?? 0) / 1e6:0.0} ms{resolved}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(data.SymbolicationNote))
                    GUILayout.Label(new GUIContent(data.SymbolicationNote, data.SymbolicationNote), EditorStyles.miniLabel, GUILayout.MaxWidth(position.width * 0.5f));
                if (GUILayout.Button("Copy Stack", EditorStyles.toolbarButton))
                {
                    var text = m_TreeView.GetSelectedStackText();
                    if (text != null)
                        EditorGUIUtility.systemCopyBuffer = text;
                }
            }
        }

        // -------------------------------------------------------------------------------------------
        // Menus & actions

        void ShowProcessMenu()
        {
            var menu = new GenericMenu();
            foreach (var (pid, name) in CaptureSession.ListGuiProcesses())
            {
                var p = pid;
                menu.AddItem(new GUIContent($"{name} ({pid})"), m_OtherTarget == p.ToString(), () => m_OtherTarget = p.ToString());
            }
            menu.ShowAsContext();
        }

        void ShowCapturesMenu()
        {
            var menu = new GenericMenu();
            var root = CaptureSession.CapturesRoot;
            var dirs = Directory.Exists(root)
                ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "time-profile.xml"))).OrderByDescending(d => d).Take(30).ToList()
                : new List<string>();
            if (dirs.Count == 0)
                menu.AddDisabledItem(new GUIContent("No captures yet"));
            foreach (var d in dirs)
            {
                var dir = d;
                menu.AddItem(new GUIContent(Path.GetFileName(dir)), dir == CaptureSession.CaptureDirectory, () => CaptureSession.OpenCapture(dir));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Reveal Captures Folder"), false, () =>
            {
                Directory.CreateDirectory(root);
                EditorUtility.RevealInFinder(root);
            });
            menu.ShowAsContext();
        }

        /// <summary>Brendan Gregg's folded format ("a;b;c weight"), loadable in speedscope.app or flamegraph.pl, with managed names.</summary>
        void ExportFolded(ProfileData data)
        {
            var path = EditorUtility.SaveFilePanel("Export folded stacks", data.CaptureDirectory, "stacks.folded", "folded");
            if (string.IsNullOrEmpty(path))
                return;

            var options = CurrentOptions();
            options.Inverted = false;
            var tree = CallTree.Build(data, options);
            var sb = new StringBuilder();
            var path0 = new List<string>();
            void Walk(CallNode n)
            {
                if (n.Id != 0)
                    path0.Add(data.Functions[n.FunctionId].Name.Replace(';', ':'));
                if (n.SelfNs > 0)
                    sb.Append(string.Join(";", path0)).Append(' ').Append(n.SelfNs / 1000).Append('\n');
                if (n.HasChildren)
                    foreach (var c in n.Children)
                        Walk(c);
                if (n.Id != 0)
                    path0.RemoveAt(path0.Count - 1);
            }
            Walk(tree.Root);
            File.WriteAllText(path, sb.ToString());
            EditorUtility.RevealInFinder(path);
        }

        /// <summary>Managed names look like "Namespace.Outer/Inner:Method": find the script and jump to the method.</summary>
        static void OpenSource(FunctionInfo fn)
        {
            if (fn.Kind != FunctionKind.Managed || fn.Name.StartsWith("(wrapper", StringComparison.Ordinal))
                return;
            var colon = fn.Name.LastIndexOf(':');
            if (colon <= 0)
                return;
            var typeName = fn.Name.Substring(0, colon);
            var method = fn.Name.Substring(colon + 1);
            var generic = method.IndexOf('<');
            if (generic > 0)
                method = method.Substring(0, generic);
            var outer = typeName.Split('/')[0];
            var className = outer.Substring(outer.LastIndexOf('.') + 1);
            var tick = className.IndexOf('`');
            if (tick > 0)
                className = className.Substring(0, tick);

            foreach (var guid in AssetDatabase.FindAssets($"{className} t:MonoScript"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(assetPath) != className)
                    continue;
                var line = 1;
                var lines = File.ReadAllLines(assetPath);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(" " + method + "(") || lines[i].Contains(" " + method + "<"))
                    {
                        line = i + 1;
                        break;
                    }
                }
                AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath), line);
                return;
            }
        }
    }
}
