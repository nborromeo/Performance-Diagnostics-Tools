using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MultiColumnViewExporter
{
    static class EditorWindowScanner
    {
        // Walks every open EditorWindow's visual tree looking for MultiColumnListView/
        // MultiColumnTreeView controls. This works for any tool's window, not just ones
        // this package knows about, because it queries the actual UI tree rather than
        // relying on reflection into a specific window type's fields.
        public static List<DiscoveredControl> ScanOpenWindows()
        {
            var results = new List<DiscoveredControl>();

            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || window.rootVisualElement == null)
                    continue;

                var listViews = window.rootVisualElement.Query<MultiColumnListView>().Build().ToList();
                for (var i = 0; i < listViews.Count; i++)
                    results.Add(DiscoveredControl.FromListView(window, listViews[i], i));

                var treeViews = window.rootVisualElement.Query<MultiColumnTreeView>().Build().ToList();
                for (var i = 0; i < treeViews.Count; i++)
                    results.Add(DiscoveredControl.FromTreeView(window, treeViews[i], i));
            }

            return results;
        }
    }
}
