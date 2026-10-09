using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace MultiColumnViewExporter
{
    public sealed class MultiColumnViewExporterWindow : EditorWindow
    {
        List<DiscoveredControl> m_Discovered = new();
        MultiColumnListView m_DiscoveryListView;
        MultiColumnListView m_ResultsListView;
        Label m_StatusLabel;
        TextField m_SqlField;
        TextField m_DbPathField;
        List<object[]> m_QueryRows = new();

        [MenuItem("Window/Analysis/MultiColumnView Exporter")]
        static void Open() => GetWindow<MultiColumnViewExporterWindow>("MultiColumnView Exporter");

        public void CreateGUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            var tabView = new TabView { style = { flexGrow = 1 } };

            var exportTab = new Tab { label = "Discover & Export" };
            exportTab.Add(BuildExportPanel());
            tabView.Add(exportTab);

            var queryTab = new Tab { label = "Query" };
            queryTab.Add(BuildQueryPanel());
            tabView.Add(queryTab);

            rootVisualElement.Add(tabView);

            m_StatusLabel = new Label();
            m_StatusLabel.style.paddingLeft = 4;
            m_StatusLabel.style.paddingBottom = 4;
            rootVisualElement.Add(m_StatusLabel);

            RefreshDiscovery();
        }

        // ── Discover & Export ────────────────────────────────────────────────
        VisualElement BuildExportPanel()
        {
            var root = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(RefreshDiscovery) { text = "Refresh" });
            toolbar.Add(new ToolbarButton(ExportSelected) { text = "Export Selected to SQLite…" });
            root.Add(toolbar);

            var columns = new Columns
            {
                new Column
                {
                    name = "selected", title = "", width = 24, resizable = false,
                    makeCell = () => new Toggle(),
                    bindCell = (el, i) =>
                    {
                        var toggle = (Toggle)el;
                        toggle.userData = i;
                        toggle.SetValueWithoutNotify(m_Discovered[i].Selected);
                        toggle.RegisterValueChangedCallback(OnSelectedToggleChanged);
                    },
                    unbindCell = (el, _) => ((Toggle)el).UnregisterValueChangedCallback(OnSelectedToggleChanged),
                },
                new Column
                {
                    name = "window", title = "Window", width = 220,
                    makeCell = () => new Label(),
                    bindCell = (el, i) => ((Label)el).text = $"{m_Discovered[i].WindowTitle} ({m_Discovered[i].WindowTypeName})",
                },
                new Column
                {
                    name = "control", title = "Control", width = 160,
                    makeCell = () => new Label(),
                    bindCell = (el, i) => ((Label)el).text =
                        (m_Discovered[i].IsTreeView ? "TreeView: " : "ListView: ") + m_Discovered[i].ControlLabel,
                },
                new Column
                {
                    name = "columns", title = "Columns", width = 220,
                    makeCell = () => new Label(),
                    bindCell = (el, i) => ((Label)el).text = string.Join(", ", m_Discovered[i].ColumnTitles),
                },
                new Column
                {
                    name = "rows", title = "Rows", width = 60,
                    makeCell = () => new Label(),
                    bindCell = (el, i) => ((Label)el).text = m_Discovered[i].RowCount.ToString(),
                },
            };

            m_DiscoveryListView = new MultiColumnListView(columns)
            {
                itemsSource = m_Discovered,
                fixedItemHeight = 20,
                style = { flexGrow = 1 },
            };
            root.Add(m_DiscoveryListView);
            return root;
        }

        void OnSelectedToggleChanged(ChangeEvent<bool> evt)
        {
            var index = (int)((Toggle)evt.target).userData;
            m_Discovered[index].Selected = evt.newValue;
        }

        void RefreshDiscovery()
        {
            m_Discovered = EditorWindowScanner.ScanOpenWindows();

            if (m_DiscoveryListView != null)
            {
                m_DiscoveryListView.itemsSource = m_Discovered;
                m_DiscoveryListView.RefreshItems();
            }

            SetStatus($"Found {m_Discovered.Count} MultiColumnListView/TreeView control(s).");
        }

        void ExportSelected()
        {
            var selected = m_Discovered.Where(d => d.Selected).ToList();
            if (selected.Count == 0)
            {
                SetStatus("No controls selected.");
                return;
            }

            var path = EditorUtility.SaveFilePanel("Export to SQLite", "", "MultiColumnViewExport.db", "db");
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var tables = selected.Select(DataExtractor.Extract).ToList();
                if (File.Exists(path))
                    File.Delete(path);
                SqliteExporter.Export(path, tables);

                if (m_DbPathField != null)
                    m_DbPathField.value = path;

                var totalRows = tables.Sum(t => t.Rows.Count);
                SetStatus($"Exported {tables.Count} table(s), {totalRows} row(s) to {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"Export failed: {ex.Message}");
            }
        }

        // ── Query ────────────────────────────────────────────────────────────
        VisualElement BuildQueryPanel()
        {
            var root = new VisualElement { style = { flexGrow = 1 } };

            var dbRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            m_DbPathField = new TextField("Database") { style = { flexGrow = 1 } };
            var browseButton = new Button(() =>
            {
                var path = EditorUtility.OpenFilePanel("Open SQLite Database", "", "db");
                if (!string.IsNullOrEmpty(path))
                    m_DbPathField.value = path;
            })
            { text = "…" };
            dbRow.Add(m_DbPathField);
            dbRow.Add(browseButton);
            root.Add(dbRow);

            m_SqlField = new TextField("SQL") { multiline = true };
            m_SqlField.style.minHeight = 60;
            root.Add(m_SqlField);

            root.Add(new Button(() => RunQuery(m_DbPathField.value)) { text = "Run Query" });

            m_ResultsListView = new MultiColumnListView(new Columns())
            {
                itemsSource = m_QueryRows,
                fixedItemHeight = 20,
                style = { flexGrow = 1 },
            };
            root.Add(m_ResultsListView);

            return root;
        }

        void RunQuery(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                SetStatus("Select a valid .db file first.");
                return;
            }

            var result = AdHocQueryRunner.Run(dbPath, m_SqlField.value);
            if (result.Error != null)
            {
                SetStatus($"Query error: {result.Error}");
                return;
            }

            m_QueryRows = result.Rows;

            m_ResultsListView.columns.Clear();
            for (var i = 0; i < result.ColumnNames.Length; i++)
            {
                var colIndex = i;
                m_ResultsListView.columns.Add(new Column
                {
                    name = "col" + colIndex,
                    title = result.ColumnNames[colIndex],
                    width = 120,
                    makeCell = () => new Label(),
                    bindCell = (el, rowIndex) => ((Label)el).text = m_QueryRows[rowIndex][colIndex]?.ToString() ?? "",
                });
            }

            m_ResultsListView.itemsSource = m_QueryRows;
            m_ResultsListView.RefreshItems();

            SetStatus($"Query returned {result.Rows.Count} row(s).");
        }

        void SetStatus(string message) => m_StatusLabel.text = message;
    }
}
