using UnityEditor;

namespace SceneBuildOptimizer
{
    public static class AssetFolderUtility
    {
        /// <summary>Creates every missing folder along an asset-relative path (e.g. "Assets/Scenes/Optimized/Level01"), via AssetDatabase so the asset database stays in sync without needing an explicit Refresh.</summary>
        public static void EnsureFolderPath(string assetFolderPath)
        {
            var parts = assetFolderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
