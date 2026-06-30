using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Profiling.BuildLogAnalyzer;

namespace BuildLogAnalyzer.Editor
{
    public class BuildLogAnalyzerWindow : EditorWindow
    {
        string m_LogFilePath = string.Empty;
        int    m_ActiveTab;
        bool   m_HasParsed;
        string m_ErrorMsg = string.Empty;

        BuildLogAnalyzerTab[] m_Tabs;

        [MenuItem("Window/Analysis/Build Log Analyzer")]
        static void Open() => GetWindow<BuildLogAnalyzerWindow>("Build Log Analyzer");

        void OnEnable()
        {
            minSize = new Vector2(640, 400);

            var shaderTab       = new ShaderCompilationTab();
            var importTab       = new AssetImportTab();
            var refreshTab      = new AssetDatabaseRefreshTab();
            var recompileTab    = new ScriptRecompilationTab();
            var addressablesTab = new AddressablesBuildTab();

            m_Tabs = new BuildLogAnalyzerTab[] { shaderTab, importTab, refreshTab, recompileTab, addressablesTab };

            // Wire up cross-tab navigation: import ↔ refresh tabs link to each other.
            int importTabIndex  = 1;
            int refreshTabIndex = 2;
            importTab.SetRefreshTabNavigation(refreshTab, () =>
            {
                m_ActiveTab = refreshTabIndex;
                Repaint();
            });
            refreshTab.SetImportTabNavigation(importTab, () =>
            {
                m_ActiveTab = importTabIndex;
                Repaint();
            });

            foreach (var tab in m_Tabs)
                tab.OnEnable(this);
        }

        void OnGUI()
        {
            DrawFilePicker();

            if (!string.IsNullOrEmpty(m_ErrorMsg))
                EditorGUILayout.HelpBox(m_ErrorMsg, MessageType.Error);

            if (!m_HasParsed)
            {
                GUILayout.Space(20f);
                GUILayout.Label("Select a log file and click Parse.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            string[] tabNames = new string[m_Tabs.Length];
            for (int i = 0; i < m_Tabs.Length; i++)
                tabNames[i] = m_Tabs[i].TabName;

            m_ActiveTab = GUILayout.Toolbar(m_ActiveTab, tabNames);
            GUILayout.Space(4f);

            string status = m_Tabs[m_ActiveTab].GetStatusMessage();
            if (!string.IsNullOrEmpty(status))
                EditorGUILayout.HelpBox(status, MessageType.Info);

            m_Tabs[m_ActiveTab].DrawGUI(position.width - 24f);
        }

        void DrawFilePicker()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Log File:", GUILayout.Width(58f));
            m_LogFilePath = EditorGUILayout.TextField(m_LogFilePath);
            if (GUILayout.Button("Browse…", EditorStyles.toolbarButton, GUILayout.Width(68f)))
            {
                string picked = EditorUtility.OpenFilePanel("Select Log File", "", "log,txt,");
                if (!string.IsNullOrEmpty(picked))
                    m_LogFilePath = picked;
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_LogFilePath)))
            {
                if (GUILayout.Button("Parse", EditorStyles.toolbarButton, GUILayout.Width(54f)))
                    ParseLog();
                if (GUILayout.Button("Generate Trace", EditorStyles.toolbarButton, GUILayout.Width(96f)))
                    GenerateTrace();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void ParseLog()
        {
            m_ErrorMsg = string.Empty;

            if (!File.Exists(m_LogFilePath))
            {
                m_ErrorMsg = $"File not found: {m_LogFilePath}";
                return;
            }

            string[] lines;
            try { lines = File.ReadAllLines(m_LogFilePath); }
            catch (Exception ex)
            {
                m_ErrorMsg = $"Could not read file: {ex.Message}";
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Build Log Analyzer", "Parsing log file…", 0f);
                foreach (var tab in m_Tabs)
                {
                    tab.Clear();
                    tab.ParseLines(lines);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            m_HasParsed = true;
            Repaint();
        }

        void GenerateTrace()
        {
            m_ErrorMsg = string.Empty;

            if (!File.Exists(m_LogFilePath))
            {
                m_ErrorMsg = $"File not found: {m_LogFilePath}";
                return;
            }

            var outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "buildLogTrace.json"));

            bool completed;
            try
            {
                var parser = new BuildLogParser(100000L);
                completed = parser.Analyze(m_LogFilePath, outputPath, progress =>
                    EditorUtility.DisplayCancelableProgressBar("Build Log Analyzer", "Generating Chrome trace…", progress));
            }
            catch (Exception ex)
            {
                m_ErrorMsg = $"Trace generation failed: {ex.Message}";
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (completed)
                EditorUtility.RevealInFinder(outputPath);
        }
    }
}
