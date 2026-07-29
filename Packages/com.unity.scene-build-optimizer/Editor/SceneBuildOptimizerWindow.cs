using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SceneBuildOptimizer.Editor
{
    public sealed class SceneBuildOptimizerWindow : EditorWindow
    {
        SceneOptimizationReport m_LastReport;
        Vector2 m_ScenesScroll;
        Vector2 m_OptimizersScroll;
        Vector2 m_OverridesScroll;
        Vector2 m_ReportScroll;

        BuildProfile m_ProfileToAdd;
        int m_OptimizerIndexToAdd;
        BuildProfile m_GenerationProfile;
        bool m_GenerationProfileInitialized;

        // IsStale() hashes the source scene and every copied asset in its manifest from disk —
        // cheap once, but OnGUI can fire dozens of times a second (any mouse move triggers a
        // repaint), and Terrain Tile Merger's manifests can list many sizable TerrainData files
        // (one per source tile consumed, not per merged output). Recomputing that on every paint
        // made the whole window noticeably sluggish even at idle, so this is cached and only
        // recomputed on triggers that could actually change the answer (see RefreshStaleCache).
        readonly System.Collections.Generic.Dictionary<string, bool> m_StaleCache = new System.Collections.Generic.Dictionary<string, bool>();

        [MenuItem("Window/Analysis/Scene Build Optimizer")]
        static void Open() => GetWindow<SceneBuildOptimizerWindow>("Scene Build Optimizer");

        void OnFocus() => m_StaleCache.Clear();

        bool IsStaleCached(string sourcePath, string optimizedPath)
        {
            if (!m_StaleCache.TryGetValue(sourcePath, out bool stale))
            {
                stale = OptimizedSceneGenerator.IsStale(sourcePath, optimizedPath);
                m_StaleCache[sourcePath] = stale;
            }
            return stale;
        }

        void OnGUI()
        {
            var settings = SceneOptimizerSettingsProvider.GetOrCreateSettings();

            DrawScenesSection(settings);
            GUILayout.Space(8);
            DrawOptimizersSection(settings);
            GUILayout.Space(8);
            DrawProfileOverridesSection(settings);
            GUILayout.Space(8);
            DrawReportSection();
        }

        // ── Tracked scenes ────────────────────────────────────────────────────
        void DrawScenesSection(SceneOptimizerSettingsContainer settings)
        {
            EditorGUILayout.LabelField("Scenes", EditorStyles.boldLabel);

            if (!m_GenerationProfileInitialized)
            {
                // A one-time convenience default so the field isn't empty on first open — after this,
                // the user's own selection sticks, it never silently follows Unity's ambient "active"
                // profile again (that was the bug: Optimize/Refresh used to resolve settings against
                // whatever profile happened to be active in the Build Profiles window, invisibly).
                m_GenerationProfile = BuildProfile.GetActiveBuildProfile();
                m_GenerationProfileInitialized = true;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Generate against profile", GUILayout.Width(150));
            m_GenerationProfile = (BuildProfile)EditorGUILayout.ObjectField(m_GenerationProfile, typeof(BuildProfile), false, GUILayout.Width(200));
            EditorGUILayout.LabelField(m_GenerationProfile == null
                ? "(project-wide defaults only)"
                : "(this profile's overrides apply)", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            var dropped = (SceneAsset)EditorGUILayout.ObjectField("Add scene", null, typeof(SceneAsset), false);
            if (dropped != null && !settings.TrackedScenes.Contains(dropped))
            {
                settings.TrackedScenes.Add(dropped);
                EditorUtility.SetDirty(settings);
            }

            if (GUILayout.Button("Add Active Scene", GUILayout.Width(130)))
            {
                var activePath = EditorSceneManager.GetActiveScene().path;
                var asset = string.IsNullOrEmpty(activePath) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(activePath);
                if (asset != null && !settings.TrackedScenes.Contains(asset))
                {
                    settings.TrackedScenes.Add(asset);
                    EditorUtility.SetDirty(settings);
                }
            }
            EditorGUILayout.EndHorizontal();

            m_ScenesScroll = EditorGUILayout.BeginScrollView(m_ScenesScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(240));

            SceneAsset toRemove = null;
            foreach (var sceneAsset in settings.TrackedScenes)
            {
                if (sceneAsset == null) continue;
                string sourcePath = AssetDatabase.GetAssetPath(sceneAsset);
                string optimizedPath = OptimizedSceneGenerator.GetDefaultOptimizedScenePath(sourcePath);
                bool exists = File.Exists(optimizedPath);
                bool stale = !exists || IsStaleCached(sourcePath, optimizedPath);

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(sceneAsset.name, GUILayout.Width(180));

                string status = !exists ? "Not generated" : stale ? "Stale" : "Up to date";
                var prevColor = GUI.color;
                GUI.color = !exists ? Color.gray : stale ? new Color(1f, 0.7f, 0.2f) : new Color(0.4f, 0.9f, 0.4f);
                EditorGUILayout.LabelField(status, GUILayout.Width(90));
                GUI.color = prevColor;

                if (GUILayout.Button(exists ? "Refresh" : "Optimize", GUILayout.Width(80)))
                {
                    m_LastReport = OptimizedSceneGenerator.Generate(sourcePath, optimizedPath, m_GenerationProfile);
                    m_StaleCache.Remove(sourcePath);
                }

                if (exists && GUILayout.Button("Ping", GUILayout.Width(50)))
                {
                    var optimizedAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(optimizedPath);
                    if (optimizedAsset != null) EditorGUIUtility.PingObject(optimizedAsset);
                }

                if (GUILayout.Button("x", GUILayout.Width(22)))
                    toRemove = sceneAsset;

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (toRemove != null)
            {
                settings.TrackedScenes.Remove(toRemove);
                EditorUtility.SetDirty(settings);
            }
        }

        // ── Optimizers ────────────────────────────────────────────────────────
        void DrawOptimizersSection(SceneOptimizerSettingsContainer settings)
        {
            EditorGUILayout.LabelField("Optimizers", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox("Project-wide default for each optimizer. A Build Profile below can override both whether it runs and its settings.", MessageType.None);

            m_OptimizersScroll = EditorGUILayout.BeginScrollView(m_OptimizersScroll, GUILayout.MinHeight(80), GUILayout.MaxHeight(160));
            foreach (var optimizer in SceneOptimizerRegistry.Optimizers)
            {
                var defaultEntry = settings.GetOrCreateDefaultEntry(optimizer.Id, optimizer.CreateDefaultSettings);

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                bool newEnabled = EditorGUILayout.ToggleLeft(optimizer.Name, defaultEntry.Enabled, GUILayout.Width(260));
                if (newEnabled != defaultEntry.Enabled)
                {
                    defaultEntry.Enabled = newEnabled;
                    EditorUtility.SetDirty(settings);
                }

                GUILayout.FlexibleSpace();

                if (optimizer.HasSettings && GUILayout.Button("⚙", GUILayout.Width(24)))
                {
                    var buttonScreenRect = GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect());
                    PopupWindow.Show(buttonScreenRect, new EntrySettingsPopup(optimizer, defaultEntry, settings));
                }

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Settings popup for a specific (default or per-profile-override) entry — edits that entry directly, not whatever's "active" right now.</summary>
        sealed class EntrySettingsPopup : PopupWindowContent
        {
            readonly ISceneOptimizer m_Optimizer;
            readonly SceneOptimizerSettingsContainer.OptimizerSettingsEntry m_Entry;
            readonly SceneOptimizerSettingsContainer m_SettingsContainer;

            public EntrySettingsPopup(ISceneOptimizer optimizer, SceneOptimizerSettingsContainer.OptimizerSettingsEntry entry, SceneOptimizerSettingsContainer settingsContainer)
            {
                m_Optimizer = optimizer;
                m_Entry = entry;
                m_SettingsContainer = settingsContainer;
            }

            public override Vector2 GetWindowSize() => new Vector2(320, 90);

            public override void OnGUI(Rect rect)
            {
                GUILayout.BeginArea(new Rect(6, 6, rect.width - 12, rect.height - 12));
                EditorGUI.BeginChangeCheck();
                m_Optimizer.DrawSettingsGUI(m_Entry.Settings);
                if (EditorGUI.EndChangeCheck())
                    EditorUtility.SetDirty(m_SettingsContainer);
                GUILayout.EndArea();
            }
        }

        // ── Build Profile overrides ──────────────────────────────────────────
        void DrawProfileOverridesSection(SceneOptimizerSettingsContainer settings)
        {
            EditorGUILayout.LabelField("Build Profile Overrides", EditorStyles.boldLabel);

            var optimizers = SceneOptimizerRegistry.Optimizers;

            m_OverridesScroll = EditorGUILayout.BeginScrollView(m_OverridesScroll, GUILayout.MinHeight(60), GUILayout.MaxHeight(160));

            BuildProfile profileToRemoveFrom = null;
            string optimizerIdToRemove = null;

            foreach (var profileOverride in settings.ProfileOverrides)
            {
                if (profileOverride.Profile == null || profileOverride.Overrides.Count == 0)
                    continue;

                EditorGUILayout.LabelField(profileOverride.Profile.name, EditorStyles.miniBoldLabel);

                foreach (var entry in profileOverride.Overrides)
                {
                    var optimizer = optimizers.FirstOrDefault(o => o.Id == entry.OptimizerId);

                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                    bool newEnabled = EditorGUILayout.ToggleLeft(
                        optimizer != null ? optimizer.Name : entry.OptimizerId, entry.Enabled, GUILayout.Width(240));
                    if (newEnabled != entry.Enabled)
                    {
                        entry.Enabled = newEnabled;
                        EditorUtility.SetDirty(settings);
                    }

                    GUILayout.FlexibleSpace();

                    if (optimizer != null && optimizer.HasSettings && GUILayout.Button("⚙", GUILayout.Width(24)))
                    {
                        var buttonScreenRect = GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect());
                        PopupWindow.Show(buttonScreenRect, new EntrySettingsPopup(optimizer, entry, settings));
                    }

                    if (GUILayout.Button("x", GUILayout.Width(22)))
                    {
                        profileToRemoveFrom = profileOverride.Profile;
                        optimizerIdToRemove = entry.OptimizerId;
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();

            if (profileToRemoveFrom != null)
            {
                settings.RemoveProfileOverride(profileToRemoveFrom, optimizerIdToRemove);
                EditorUtility.SetDirty(settings);
            }

            GUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            m_ProfileToAdd = (BuildProfile)EditorGUILayout.ObjectField(m_ProfileToAdd, typeof(BuildProfile), false, GUILayout.Width(200));

            if (optimizers.Count == 0)
            {
                EditorGUILayout.LabelField("No optimizers registered", EditorStyles.miniLabel);
            }
            else
            {
                m_OptimizerIndexToAdd = Mathf.Clamp(m_OptimizerIndexToAdd, 0, optimizers.Count - 1);
                var optimizerNames = optimizers.Select(o => o.Name).ToArray();
                m_OptimizerIndexToAdd = EditorGUILayout.Popup(m_OptimizerIndexToAdd, optimizerNames, GUILayout.Width(180));

                using (new EditorGUI.DisabledScope(m_ProfileToAdd == null))
                {
                    if (GUILayout.Button("Add Override", GUILayout.Width(100)))
                    {
                        var optimizer = optimizers[m_OptimizerIndexToAdd];
                        settings.GetOrCreateProfileOverride(m_ProfileToAdd, optimizer.Id, optimizer.CreateDefaultSettings);
                        EditorUtility.SetDirty(settings);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Report ────────────────────────────────────────────────────────────
        void DrawReportSection()
        {
            EditorGUILayout.LabelField("Last Run", EditorStyles.boldLabel);

            if (m_LastReport == null)
            {
                EditorGUILayout.HelpBox("Run \"Optimize\" on a scene above to see its report here.", MessageType.Info);
                return;
            }

            m_ReportScroll = EditorGUILayout.BeginScrollView(m_ReportScroll, GUILayout.MinHeight(80));
            if (m_LastReport.Entries.Count == 0)
            {
                EditorGUILayout.LabelField("No changes — nothing to optimize.", EditorStyles.miniLabel);
            }
            foreach (var entry in m_LastReport.Entries)
            {
                var type = entry.IsWarning ? MessageType.Warning : MessageType.None;
                EditorGUILayout.HelpBox($"[{entry.OptimizerName}] {entry.Message}", type);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
