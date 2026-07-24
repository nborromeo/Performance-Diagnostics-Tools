using System.IO;
using UnityEditor;
using UnityEngine;

namespace SceneBuildOptimizer
{
    public static class AssetCopyUtility
    {
        /// <summary>
        /// Copies <paramref name="sourcePath"/>'s content to <paramref name="destPath"/>. If an asset
        /// already exists at destPath, its file content is overwritten in place rather than deleted
        /// and recreated — deleting an asset removes its .meta file, so a fresh AssetDatabase.CopyAsset
        /// at the same path gets a brand new GUID, silently breaking any existing reference to it (e.g.
        /// a scene already listed in a BuildProfile's scene override list, which references scenes by
        /// GUID, not path).
        /// </summary>
        public static bool CopyOrOverwrite(string sourcePath, string destPath)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(destPath) != null)
            {
                File.Copy(sourcePath, destPath, true);
                AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceUpdate);
                return true;
            }

            return AssetDatabase.CopyAsset(sourcePath, destPath);
        }

        /// <summary>
        /// Always deletes any existing asset at destPath first, then does a fresh AssetDatabase.CopyAsset
        /// — a new GUID every time. Use this (rather than <see cref="CopyOrOverwrite"/>) for assets with
        /// complex embedded sub-assets (e.g. TerrainData's alphamap Texture2Ds), where a raw file
        /// overwrite + forced reimport isn't reliably guaranteed to refresh already-loaded in-memory
        /// sub-object state cleanly — and where nothing outside the current regeneration holds a
        /// persistent reference to the copy's GUID anyway (unlike, say, an optimized scene, which a
        /// BuildProfile keeps referencing by GUID across regenerations).
        /// </summary>
        public static bool ForceFreshCopy(string sourcePath, string destPath)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(destPath) != null)
                AssetDatabase.DeleteAsset(destPath);

            return AssetDatabase.CopyAsset(sourcePath, destPath);
        }
    }
}
