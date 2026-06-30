using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BuildLogAnalyzer.Editor
{
    sealed class AddressablesBuildTab : BuildLogAnalyzerTab
    {
        // ── Data model ────────────────────────────────────────────────────────

        sealed class AddressablesBuildEntry
        {
            public int    LineNumber;
            public float  DurationSec;
            public string DurationDisplay;
        }

        sealed class AddressablesTreeView : TreeView<int>
        {
            List<AddressablesBuildEntry> m_Source = new List<AddressablesBuildEntry>();

            public AddressablesTreeView(TreeViewState<int> state, MultiColumnHeader header) : base(state, header)
            {
                rowHeight                     = 18f;
                showAlternatingRowBackgrounds = true;
                showBorder                    = true;
                header.sortingChanged         += _ => { SortSource(); Reload(); };
                Reload();
            }

            public void SetSource(List<AddressablesBuildEntry> entries)
            {
                m_Source = entries;
                SortSource();
                Reload();
            }

            void SortSource()
            {
                int col = multiColumnHeader.sortedColumnIndex;
                if (col < 0 || m_Source.Count == 0) return;
                bool asc = multiColumnHeader.IsSortedAscending(col);
                m_Source.Sort((a, b) =>
                {
                    int cmp = col switch
                    {
                        0 => a.LineNumber.CompareTo(b.LineNumber),
                        1 => a.DurationSec.CompareTo(b.DurationSec),
                        _ => 0
                    };
                    return asc ? cmp : -cmp;
                });
            }

            protected override TreeViewItem<int> BuildRoot()
            {
                var root  = new TreeViewItem<int>(-1, -1);
                var items = new List<TreeViewItem<int>>(m_Source.Count);
                for (int i = 0; i < m_Source.Count; i++)
                    items.Add(new TreeViewItem<int>(i, 0, m_Source[i].DurationDisplay));
                SetupParentsAndChildrenFromDepths(root, items);
                return root;
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var e = m_Source[args.item.id];
                for (int i = 0; i < args.GetNumVisibleColumns(); i++)
                {
                    var rect = args.GetCellRect(i);
                    CenterRectUsingSingleLineHeight(ref rect);
                    string text = args.GetColumn(i) switch
                    {
                        0 => e.LineNumber.ToString(),
                        1 => e.DurationDisplay,
                        _ => string.Empty
                    };
                    EditorGUI.LabelField(rect, text);
                }
            }

            public static MultiColumnHeaderState CreateDefaultHeaderState()
            {
                var state = new MultiColumnHeaderState(new[]
                {
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Line",     "Log file line where the Addressables build started"),  width = 55,  minWidth = 40, autoResize = false, canSort = true, allowToggleVisibility = false },
                    new MultiColumnHeaderState.Column { headerContent = new GUIContent("Duration", "Total build duration"),                                 width = 120, minWidth = 60, autoResize = true,  canSort = true, allowToggleVisibility = false },
                });
                state.sortedColumnIndex          = 0;
                state.columns[0].sortedAscending = true;
                return state;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<AddressablesBuildEntry> m_Entries = new List<AddressablesBuildEntry>();
        string              m_StatusMsg  = string.Empty;
        string              m_ParseExtra = string.Empty;
        AddressablesTreeView m_TreeView;
        TreeViewState<int>  m_TreeState;

        // ── BuildLogAnalyzerTab ───────────────────────────────────────────────

        public override string TabName => "Addressables Builds";

        public override void Clear()
        {
            m_Entries.Clear();
            m_StatusMsg = string.Empty;
        }

        public override string GetStatusMessage() => m_StatusMsg;

        public override void ParseLines(string[] lines)
        {
            const string k_Start   = "DisplayProgressbar: Processing Addressable Group";
            const string k_Success = "Addressable content successfully built (duration : ";
            const string k_Failure = "Addressable content build failure (duration : ";
            const string k_Close   = ")";

            int pendingStartLine = -1;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string raw = lines[lineIdx];

                if (raw.IndexOf(k_Start, StringComparison.Ordinal) >= 0)
                {
                    // Only record the first occurrence; later progress updates are ignored.
                    if (pendingStartLine < 0)
                        pendingStartLine = lineIdx + 1;
                    continue;
                }

                string durationMarker = null;
                if      (raw.IndexOf(k_Success, StringComparison.Ordinal) >= 0) durationMarker = k_Success;
                else if (raw.IndexOf(k_Failure, StringComparison.Ordinal) >= 0) durationMarker = k_Failure;

                if (durationMarker != null && pendingStartLine >= 0)
                {
                    int ds = raw.IndexOf(durationMarker, StringComparison.Ordinal) + durationMarker.Length;
                    int de = raw.IndexOf(k_Close, ds, StringComparison.Ordinal);
                    if (de > ds)
                    {
                        string durStr = raw.Substring(ds, de - ds).Trim();
                        m_Entries.Add(new AddressablesBuildEntry
                        {
                            LineNumber      = pendingStartLine,
                            DurationSec     = ParseDuration(durStr),
                            DurationDisplay = durStr,
                        });
                    }
                    pendingStartLine = -1;
                }
            }

            float totalSec = 0f;
            foreach (var e in m_Entries) totalSec += e.DurationSec;
            m_ParseExtra = $"  |  Total Build Time: {FormatDuration(totalSec)}";

            EnsureTreeView();
            m_TreeView.SetSource(m_Entries);

            m_StatusMsg = $"Showing {m_Entries.Count} build(s){m_ParseExtra}";
        }

        public override void DrawGUI(float contentWidth)
        {
            if (m_Entries.Count == 0)
            {
                GUILayout.Label("No Addressables build entries found.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EnsureTreeView();
            Rect treeRect = GUILayoutUtility.GetRect(contentWidth, 50f, GUILayout.ExpandHeight(true));
            m_TreeView.OnGUI(treeRect);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        void EnsureTreeView()
        {
            if (m_TreeView != null) return;
            if (m_TreeState == null) m_TreeState = new TreeViewState<int>();
            m_TreeView = new AddressablesTreeView(m_TreeState, new MultiColumnHeader(AddressablesTreeView.CreateDefaultHeaderState()));
        }

        // Parses "H:MM:SS.mmm" into total seconds.
        static float ParseDuration(string s)
        {
            // Expected format: H:MM:SS.mmm  (hours may be multi-digit)
            int firstColon  = s.IndexOf(':');
            int secondColon = firstColon >= 0 ? s.IndexOf(':', firstColon + 1) : -1;
            if (firstColon < 0 || secondColon < 0) return 0f;

            if (!int.TryParse(s.Substring(0, firstColon), out int hours)) return 0f;
            if (!int.TryParse(s.Substring(firstColon + 1, secondColon - firstColon - 1), out int minutes)) return 0f;
            if (!float.TryParse(s.Substring(secondColon + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float secs)) return 0f;

            return hours * 3600f + minutes * 60f + secs;
        }

        static string FormatDuration(float seconds)
        {
            int totalSec = Mathf.FloorToInt(seconds);
            if (totalSec < 60) return $"{seconds:F3}s";
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            return h > 0 ? $"{h}h {m}m {s}s" : $"{m}m {s}s";
        }
    }
}
