using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Sidecar record describing how an optimized scene was generated, saved as JSON next to the
    /// optimized scene asset (e.g. "Level01.optimized.json"). Lets <see cref="OptimizedSceneGenerator"/>
    /// and the pre-build check cheaply tell "nothing changed, skip" from "source changed, regenerate"
    /// without re-running detection.
    /// </summary>
    [Serializable]
    public sealed class OptimizedSceneManifest
    {
        [Serializable]
        public struct CopiedAsset
        {
            public string SourcePath;
            public string OptimizedPath;
            public string SourceHash;
        }

        public string SourceScenePath;
        public string SourceSceneHash;
        public string OptimizedScenePath;
        public List<CopiedAsset> CopiedAssets = new List<CopiedAsset>();

        public static string GetManifestPath(string optimizedScenePath) =>
            Path.ChangeExtension(optimizedScenePath, null) + ".optimized.json";

        public static OptimizedSceneManifest Load(string optimizedScenePath)
        {
            var path = GetManifestPath(optimizedScenePath);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<OptimizedSceneManifest>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Scene Build Optimizer: failed to read manifest at {path}: {e.Message}");
                return null;
            }
        }

        public void Save(string optimizedScenePath)
        {
            OptimizedScenePath = optimizedScenePath;
            File.WriteAllText(GetManifestPath(optimizedScenePath), JsonUtility.ToJson(this, true));
        }

        /// <summary>Stable content hash for staleness checks — file mtime is unreliable across machines/CI, so hash the bytes instead.</summary>
        public static string ComputeFileHash(string assetPath)
        {
            if (!File.Exists(assetPath)) return null;
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            using var stream = File.OpenRead(assetPath);
            return Convert.ToBase64String(sha1.ComputeHash(stream));
        }

        /// <summary>True if the source scene or any copied source asset has changed since this manifest was generated.</summary>
        public bool IsStale()
        {
            if (SourceSceneHash != ComputeFileHash(SourceScenePath))
                return true;

            foreach (var copied in CopiedAssets)
            {
                if (copied.SourceHash != ComputeFileHash(copied.SourcePath))
                    return true;
            }

            return false;
        }
    }
}
