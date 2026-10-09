using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace NativeProfiler
{
    /// <summary>
    /// Multi-column view over a <see cref="CallTree"/>. Rows are generated lazily (only expanded
    /// nodes get TreeViewItems) because a few seconds of sampling easily produce 100k+ nodes.
    /// </summary>
    internal sealed class CallTreeView : TreeView<int>
    {
        enum Column { Total, Percent, Self, Function, Module }

        const int k_MaxSearchResults = 5000;

        CallTree m_Tree;
        ProfileData m_Data;
        readonly List<TreeViewItem<int>> m_Rows = new List<TreeViewItem<int>>(1024);

        // Horizontal offset applied only inside the Function column, so deep stacks can be scrolled
        // without pushing the timing columns out of view.
        float m_FunctionScroll;
        // Widest unscrolled indent + label among the rows drawn in the last repaint.
        float m_FunctionContentWidth;
        // Function column left edge in row coordinates (foldouts left of it are scrolled out).
        float m_FunctionCellX;
        static readonly GUIContent s_TempContent = new GUIContent();

        public Action<FunctionInfo> FunctionDoubleClicked;

        public CallTreeView(TreeViewState<int> state, MultiColumnHeader header) : base(state, header)
        {
            showAlternatingRowBackgrounds = true;
            showBorder = true;
            rowHeight = 18;
            columnIndexForTreeFoldouts = (int)Column.Function;
            foldoutOverride = (position, expanded, style) =>
                position.xMin < m_FunctionCellX ? expanded : GUI.Toggle(position, expanded, GUIContent.none, style);
            Reload();
        }

        /// <summary>
        /// Header with drag-to-reorder columns. Toggling visibility keeps the user's order (the base
        /// implementation re-sorts visible columns by definition order).
        /// </summary>
        public sealed class Header : MultiColumnHeader
        {
            public Header(MultiColumnHeaderState state) : base(state)
            {
                allowDraggingColumnsToReorder = true;
            }

            protected override void ToggleVisibility(int columnIndex)
            {
                var visible = new List<int>(state.visibleColumns);
                if (!visible.Remove(columnIndex))
                    visible.Add(columnIndex);
                state.visibleColumns = visible.ToArray();
                Repaint();
                OnVisibleColumnsChanged();
            }
        }

        public static MultiColumnHeaderState CreateHeaderState()
        {
            MultiColumnHeaderState.Column Col(string title, float width, bool autoResize = false, TextAlignment align = TextAlignment.Right) =>
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent(title),
                    width = width,
                    minWidth = 40,
                    autoResize = autoResize,
                    canSort = false,
                    headerTextAlignment = align,
                    allowToggleVisibility = true,
                };

            return new MultiColumnHeaderState(new[]
            {
                Col("Total (ms)", 80),
                Col("%", 55),
                Col("Self (ms)", 75),
                Col("Function", 600, true, TextAlignment.Left),
                Col("Module", 170, false, TextAlignment.Left),
            });
        }

        public CallTree Tree => m_Tree;

        public void SetTree(ProfileData data, CallTree tree, bool expandHeaviest)
        {
            m_Data = data;
            m_Tree = tree;
            m_FunctionScroll = 0;
            state.expandedIDs.Clear();
            state.selectedIDs.Clear();
            Reload();
            if (expandHeaviest)
                ExpandHeaviestPath();
        }

        public void ExpandHeaviestPath()
        {
            if (m_Tree?.Root?.Children == null)
                return;
            var node = m_Tree.Root.Children[0];
            var last = node;
            // Keep walking while the heaviest child still accounts for most of its parent's time.
            while (node != null)
            {
                SetExpanded(node.Id, true);
                last = node;
                if (!node.HasChildren)
                    break;
                var next = node.Children[0];
                node = next.TotalNs * 4 >= node.TotalNs ? next : null;
            }
            SetSelection(new[] { last.Id }, TreeViewSelectionOptions.RevealAndFrame);
            RevealInFunctionColumn(last.Id);
        }

        /// <summary>
        /// Draws the tree plus a scrollbar under the Function column that scrolls only that column.
        /// Shift+wheel or a horizontal trackpad swipe over the Function column scrolls it too.
        /// </summary>
        public void Draw(Rect rect)
        {
            var barHeight = GUI.skin.horizontalScrollbar.fixedHeight > 0 ? GUI.skin.horizontalScrollbar.fixedHeight : 15;
            var barRect = new Rect(rect.x, rect.yMax - barHeight, rect.width, barHeight);
            rect.yMax = barRect.y;

            var colRect = FunctionColumnRect(out var funcIndex);
            m_FunctionCellX = colRect.x;
            var colX = rect.x + colRect.x - state.scrollPos.x;

            var e = Event.current;
            if (funcIndex >= 0 && e.type == EventType.ScrollWheel && e.mousePosition.x >= colX && e.mousePosition.x < colX + colRect.width
                && rect.Contains(e.mousePosition) && (e.shift || Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y)))
            {
                var delta = Mathf.Abs(e.delta.x) > 0 ? e.delta.x : e.delta.y;
                SetFunctionScroll(m_FunctionScroll + delta * 10, colRect.width);
                e.Use();
                Repaint();
            }

            baseIndent = -m_FunctionScroll;
            if (e.type == EventType.Repaint)
                m_FunctionContentWidth = 0;
            OnGUI(rect);

            if (funcIndex < 0)
                return;
            var visibleWidth = colRect.width;
            var bar = Rect.MinMaxRect(Mathf.Max(barRect.xMin, colX), barRect.yMin, Mathf.Min(barRect.xMax, colX + visibleWidth), barRect.yMax);
            if (bar.width < 20)
                return;
            var contentWidth = Mathf.Max(m_FunctionContentWidth + 20, m_FunctionScroll + visibleWidth);
            var scroll = GUI.HorizontalScrollbar(bar, m_FunctionScroll, visibleWidth, 0, contentWidth);
            if (!Mathf.Approximately(scroll, m_FunctionScroll))
            {
                m_FunctionScroll = scroll;
                Repaint();
            }
        }

        /// <summary>
        /// Function column rect in header space, computed from the header state. Don't use
        /// MultiColumnHeader.GetColumnRect here: its rects only exist after the header's first OnGUI,
        /// and Draw/RevealInFunctionColumn run before that.
        /// </summary>
        Rect FunctionColumnRect(out int visibleIndex)
        {
            visibleIndex = multiColumnHeader.GetVisibleColumnIndex((int)Column.Function);
            if (visibleIndex < 0)
                return default;
            var headerState = multiColumnHeader.state;
            var x = 0f;
            for (var i = 0; i < visibleIndex; i++)
                x += headerState.columns[headerState.visibleColumns[i]].width;
            return new Rect(x, 0, headerState.columns[(int)Column.Function].width, multiColumnHeader.height);
        }

        void SetFunctionScroll(float value, float visibleWidth) =>
            m_FunctionScroll = Mathf.Clamp(value, 0, Mathf.Max(m_FunctionScroll, m_FunctionContentWidth + 20 - visibleWidth));

        /// <summary>Scrolls the Function column so the given node's label is in view.</summary>
        void RevealInFunctionColumn(int id)
        {
            var width = FunctionColumnRect(out var funcIndex).width;
            if (funcIndex < 0)
                return;
            var depth = -1;
            if (!hasSearch)
                for (var n = m_Tree?.Find(id); n != null && n.Id != 0; n = n.Parent)
                    depth++;
            var indent = Mathf.Max(0, depth) * depthIndentWidth + foldoutWidth;
            if (indent < m_FunctionScroll || indent > m_FunctionScroll + width - 150)
                m_FunctionScroll = Mathf.Max(0, indent - width * 0.25f);
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            if (selectedIds.Count > 0)
                RevealInFunctionColumn(selectedIds[0]);
        }

        protected override TreeViewItem<int> BuildRoot() => new TreeViewItem<int>(0, -1, "Root") { children = new List<TreeViewItem<int>>() };

        protected override IList<TreeViewItem<int>> BuildRows(TreeViewItem<int> root)
        {
            m_Rows.Clear();
            root.children = new List<TreeViewItem<int>>();
            if (m_Tree?.Root == null)
                return m_Rows;

            if (hasSearch)
            {
                foreach (var node in m_Tree.Nodes)
                {
                    if (m_Rows.Count >= k_MaxSearchResults)
                        break;
                    if (m_Data.Functions[node.FunctionId].Name.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var item = new TreeViewItem<int>(node.Id, 0) { parent = root };
                    root.children.Add(item);
                    m_Rows.Add(item);
                }
                m_Rows.Sort((a, b) => m_Tree.Find(b.id).TotalNs.CompareTo(m_Tree.Find(a.id).TotalNs));
                return m_Rows;
            }

            AddChildren(root, m_Tree.Root, 0);
            return m_Rows;
        }

        void AddChildren(TreeViewItem<int> parentItem, CallNode parentNode, int depth)
        {
            if (!parentNode.HasChildren)
                return;
            parentItem.children = new List<TreeViewItem<int>>(parentNode.Children.Count);
            foreach (var child in parentNode.Children)
            {
                var item = new TreeViewItem<int>(child.Id, depth) { parent = parentItem };
                parentItem.children.Add(item);
                m_Rows.Add(item);
                if (child.HasChildren)
                {
                    if (IsExpanded(child.Id))
                        AddChildren(item, child, depth + 1);
                    else
                        item.children = CreateChildListForCollapsedParent();
                }
            }
        }

        protected override IList<int> GetAncestors(int id)
        {
            var result = new List<int>();
            for (var n = m_Tree?.Find(id)?.Parent; n != null && n.Id != 0; n = n.Parent)
                result.Add(n.Id);
            return result;
        }

        protected override IList<int> GetDescendantsThatHaveChildren(int id)
        {
            var result = new List<int>();
            var start = id == 0 ? m_Tree?.Root : m_Tree?.Find(id);
            if (start == null)
                return result;
            var stack = new Stack<CallNode>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!n.HasChildren)
                    continue;
                if (n.Id != 0)
                    result.Add(n.Id);
                foreach (var c in n.Children)
                    stack.Push(c);
            }
            return result;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            var node = m_Tree.Find(args.item.id);
            if (node == null)
                return;
            var fn = m_Data.Functions[node.FunctionId];
            var total = m_Tree.TotalNs > 0 ? m_Tree.TotalNs : 1;

            for (var i = 0; i < args.GetNumVisibleColumns(); i++)
            {
                var rect = args.GetCellRect(i);
                CenterRectUsingSingleLineHeight(ref rect);
                switch ((Column)args.GetColumn(i))
                {
                    case Column.Total:
                        DefaultGUI.LabelRightAligned(rect, Ms(node.TotalNs), args.selected, args.focused);
                        break;
                    case Column.Percent:
                        DrawPercent(rect, node.TotalNs / (double)total, args);
                        break;
                    case Column.Self:
                        DefaultGUI.LabelRightAligned(rect, node.SelfNs > 0 ? Ms(node.SelfNs) : "", args.selected, args.focused);
                        break;
                    case Column.Function:
                    {
                        // Content indent already includes -m_FunctionScroll (via baseIndent); clip to the cell
                        // so scrolled-out text doesn't spill into the columns on the left.
                        var indent = GetContentIndent(args.item);
                        var style = args.selected || fn.Kind == FunctionKind.Native ? null : KindStyle(fn.Kind);
                        if (Event.current.type == EventType.Repaint)
                        {
                            s_TempContent.text = fn.Name;
                            var labelWidth = (style ?? EditorStyles.label).CalcSize(s_TempContent).x;
                            m_FunctionContentWidth = Mathf.Max(m_FunctionContentWidth, indent + m_FunctionScroll + labelWidth);
                        }
                        if (indent >= rect.width)
                            break;
                        GUI.BeginClip(rect);
                        var local = new Rect(indent, 0, rect.width - indent, rect.height);
                        if (style == null)
                            DefaultGUI.Label(local, fn.Name, args.selected, args.focused);
                        else
                            GUI.Label(local, fn.Name, style);
                        GUI.EndClip();
                        break;
                    }
                    case Column.Module:
                        DefaultGUI.Label(rect, fn.Module, args.selected, args.focused);
                        break;
                }
            }
        }

        protected override void DoubleClickedItem(int id)
        {
            var node = m_Tree?.Find(id);
            if (node != null)
                FunctionDoubleClicked?.Invoke(m_Data.Functions[node.FunctionId]);
        }

        protected override void SearchChanged(string newSearch)
        {
            m_FunctionScroll = 0;
            // Leaving search: reveal what was selected in the flat result list.
            if (string.IsNullOrEmpty(newSearch) && state.selectedIDs.Count > 0)
                EditorApplication.delayCall += () => SetSelection(state.selectedIDs, TreeViewSelectionOptions.RevealAndFrame);
        }

        public string GetSelectedStackText()
        {
            var sel = GetSelection();
            if (sel.Count == 0)
                return null;
            var lines = new List<string>();
            for (var n = m_Tree.Find(sel[0]); n != null && n.Id != 0; n = n.Parent)
                lines.Add(m_Data.Functions[n.FunctionId].Name);
            lines.Reverse();
            return string.Join("\n", lines);
        }

        static void DrawPercent(Rect rect, double fraction, RowGUIArgs args)
        {
            var bar = rect;
            bar.width = Mathf.Max(0, (float)(rect.width * fraction));
            EditorGUI.DrawRect(bar, new Color(1f, 0.45f, 0.1f, 0.35f));
            DefaultGUI.LabelRightAligned(rect, (fraction * 100).ToString("0.0"), args.selected, args.focused);
        }

        static string Ms(long ns) => (ns / 1e6).ToString("0.0");

        static readonly Dictionary<FunctionKind, GUIStyle> s_KindStyles = new Dictionary<FunctionKind, GUIStyle>();

        static GUIStyle KindStyle(FunctionKind kind)
        {
            if (s_KindStyles.TryGetValue(kind, out var style))
                return style;
            var pro = EditorGUIUtility.isProSkin;
            Color c;
            switch (kind)
            {
                case FunctionKind.Managed: c = pro ? new Color(0.55f, 0.85f, 1f) : new Color(0.05f, 0.35f, 0.7f); break;
                case FunctionKind.Synthetic: c = pro ? new Color(1f, 0.8f, 0.4f) : new Color(0.55f, 0.35f, 0f); break;
                default: c = new Color(0.5f, 0.5f, 0.5f); break;
            }
            style = new GUIStyle(EditorStyles.label) { normal = { textColor = c } };
            s_KindStyles[kind] = style;
            return style;
        }
    }
}
