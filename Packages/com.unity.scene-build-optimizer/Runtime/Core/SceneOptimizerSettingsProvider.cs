using UnityEditor;
using UnityEngine;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Loads (or lazily creates) the project's single <see cref="SceneOptimizerSettingsContainer"/>,
    /// registered via <see cref="EditorBuildSettings.AddConfigObject"/> — the same mechanism
    /// Addressables/NavMesh use for package-owned project settings, so the asset can live anywhere
    /// the user wants without a hardcoded path.
    /// </summary>
    public static class SceneOptimizerSettingsProvider
    {
        const string k_ConfigKey = "com.unity.scene-build-optimizer.Settings";
        const string k_DefaultAssetDir = "Assets/Editor";
        const string k_DefaultAssetPath = k_DefaultAssetDir + "/SceneBuildOptimizerSettings.asset";

        static SceneOptimizerSettingsContainer s_Cached;

        public static SceneOptimizerSettingsContainer GetOrCreateSettings()
        {
            if (s_Cached != null)
                return s_Cached;

            if (EditorBuildSettings.TryGetConfigObject(k_ConfigKey, out SceneOptimizerSettingsContainer settings) && settings != null)
                return s_Cached = settings;

            settings = AssetDatabase.LoadAssetAtPath<SceneOptimizerSettingsContainer>(k_DefaultAssetPath);
            if (settings == null)
            {
                AssetFolderUtility.EnsureFolderPath(k_DefaultAssetDir);

                settings = ScriptableObject.CreateInstance<SceneOptimizerSettingsContainer>();
                AssetDatabase.CreateAsset(settings, k_DefaultAssetPath);
                AssetDatabase.SaveAssets();
            }

            EditorBuildSettings.AddConfigObject(k_ConfigKey, settings, true);
            return s_Cached = settings;
        }
    }
}
