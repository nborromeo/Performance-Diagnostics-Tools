using System;
using System.Collections.Generic;

namespace SceneBuildOptimizer
{
    /// <summary>Per-run log of what an <see cref="OptimizedSceneGenerator"/> run did, surfaced in the window.</summary>
    public sealed class SceneOptimizationReport
    {
        public readonly struct Entry
        {
            public readonly string OptimizerName;
            public readonly string Message;
            public readonly bool IsWarning;

            public Entry(string optimizerName, string message, bool isWarning)
            {
                OptimizerName = optimizerName;
                Message = message;
                IsWarning = isWarning;
            }
        }

        public readonly struct CopiedAsset
        {
            public readonly string SourcePath;
            public readonly string OptimizedPath;

            public CopiedAsset(string sourcePath, string optimizedPath)
            {
                SourcePath = sourcePath;
                OptimizedPath = optimizedPath;
            }
        }

        readonly List<Entry> m_Entries = new List<Entry>();
        readonly List<CopiedAsset> m_CopiedAssets = new List<CopiedAsset>();

        public IReadOnlyList<Entry> Entries => m_Entries;
        public IReadOnlyList<CopiedAsset> CopiedAssets => m_CopiedAssets;

        public DateTime GeneratedAtUtc { get; private set; } = DateTime.UtcNow;

        public void LogChange(string optimizerName, string message) => m_Entries.Add(new Entry(optimizerName, message, false));

        public void LogWarning(string optimizerName, string message) => m_Entries.Add(new Entry(optimizerName, message, true));

        /// <summary>Records that an optimizer duplicated <paramref name="sourcePath"/> to <paramref name="optimizedPath"/>, so the generator can track it in the scene's manifest for staleness checks.</summary>
        public void LogCopiedAsset(string sourcePath, string optimizedPath) => m_CopiedAssets.Add(new CopiedAsset(sourcePath, optimizedPath));
    }
}
