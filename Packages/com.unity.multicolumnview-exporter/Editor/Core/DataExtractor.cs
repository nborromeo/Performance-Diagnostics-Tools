using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.UIElements;

namespace MultiColumnViewExporter
{
    sealed class ExtractedTable
    {
        public string TableName;
        public List<ColumnDef> Columns;
        public List<object[]> Rows;
    }

    // Reads the raw row data behind a discovered control, independent of how its
    // columns render cells.
    static class DataExtractor
    {
        public static ExtractedTable Extract(DiscoveredControl control)
        {
            return control.IsTreeView ? ExtractTree(control) : ExtractList(control);
        }

        static ExtractedTable ExtractList(DiscoveredControl control)
        {
            var columns = new List<ColumnDef>();
            List<MemberInfo> members = null;
            var rows = new List<object[]>();

            if (control.ListView.itemsSource is IList source)
            {
                foreach (var item in source)
                {
                    if (members == null)
                        (columns, members) = RowReflector.BuildSchema(item);
                    rows.Add(RowReflector.ExtractValues(item, members));
                }
            }

            return new ExtractedTable { TableName = control.SuggestedTableName(), Columns = columns, Rows = rows };
        }

        static ExtractedTable ExtractTree(DiscoveredControl control)
        {
            var tv = control.TreeView;
            var columns = new List<ColumnDef>();
            List<MemberInfo> members = null;
            var rows = new List<object[]>();

            var controller = tv.viewController as BaseTreeViewController;
            var count = (tv.itemsSource as IList)?.Count ?? 0;

            for (var i = 0; i < count; i++)
            {
                object item;
                try
                {
                    item = tv.GetItemDataForIndex<object>(i);
                }
                catch
                {
                    continue;
                }

                if (members == null)
                {
                    (columns, members) = RowReflector.BuildSchema(item);
                    columns.Insert(0, new ColumnDef { Name = "parent_id", SqlType = "INTEGER" });
                    columns.Insert(0, new ColumnDef { Name = "row_id", SqlType = "INTEGER" });
                }

                int id = -1, parentId = -1;
                try
                {
                    if (controller != null)
                    {
                        id = controller.GetIdForIndex(i);
                        parentId = controller.GetParentId(id);
                    }
                }
                catch
                {
                    // Fall back to a flat table (no hierarchy) if the controller API
                    // isn't available on this Editor version.
                }

                var values = RowReflector.ExtractValues(item, members);
                var full = new object[values.Length + 2];
                full[0] = id;
                full[1] = parentId;
                Array.Copy(values, 0, full, 2, values.Length);
                rows.Add(full);
            }

            return new ExtractedTable { TableName = control.SuggestedTableName(), Columns = columns, Rows = rows };
        }
    }
}
