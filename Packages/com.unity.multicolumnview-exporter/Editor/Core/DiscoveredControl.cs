using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine.UIElements;

namespace MultiColumnViewExporter
{
    sealed class DiscoveredControl
    {
        public EditorWindow Window;
        public string WindowTitle;
        public string WindowTypeName;
        public MultiColumnListView ListView;
        public MultiColumnTreeView TreeView;
        public string ControlLabel;
        public List<string> ColumnTitles;
        public int RowCount;
        public bool Selected;

        public bool IsTreeView => TreeView != null;

        public static DiscoveredControl FromListView(EditorWindow window, MultiColumnListView view, int index)
        {
            return new DiscoveredControl
            {
                Window = window,
                WindowTitle = window.titleContent.text,
                WindowTypeName = window.GetType().Name,
                ListView = view,
                ControlLabel = string.IsNullOrEmpty(view.name) ? $"MultiColumnListView #{index}" : view.name,
                ColumnTitles = ColumnTitlesOf(view.columns),
                RowCount = (view.itemsSource as IList)?.Count ?? 0,
            };
        }

        public static DiscoveredControl FromTreeView(EditorWindow window, MultiColumnTreeView view, int index)
        {
            return new DiscoveredControl
            {
                Window = window,
                WindowTitle = window.titleContent.text,
                WindowTypeName = window.GetType().Name,
                TreeView = view,
                ControlLabel = string.IsNullOrEmpty(view.name) ? $"MultiColumnTreeView #{index}" : view.name,
                ColumnTitles = ColumnTitlesOf(view.columns),
                RowCount = (view.itemsSource as IList)?.Count ?? 0,
            };
        }

        static List<string> ColumnTitlesOf(Columns columns) =>
            columns.Select(c => string.IsNullOrEmpty(c.title) ? c.name : c.title).ToList();

        public string SuggestedTableName()
        {
            var sb = new StringBuilder();
            foreach (var ch in $"{WindowTypeName}_{ControlLabel}")
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            var name = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(name) ? "Table" : name;
        }
    }
}
