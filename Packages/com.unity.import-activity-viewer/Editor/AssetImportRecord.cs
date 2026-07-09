using System;
using System.Collections.Generic;

namespace ImportActivityViewer.Editor
{
    internal sealed class DependencyRef
    {
        public string Key;
        public bool IsDynamic;
    }

    internal sealed class AssetImportRecord
    {
        public string AssetPath;
        public string ArtifactKey;
        public string ImporterName;
        public long ImportDurationMicroseconds;
        public string TimeStampDisplay;
        public DateTime? ImportedAt;
        public ulong EditorRevision;
        public bool HasEditorRevision;
        public readonly List<DependencyRef> Dependencies = new List<DependencyRef>();

        // Deterministic cause of reimport, taken from Unity's own ArtifactDifferenceReporter output
        // (the same text the built-in Import Activity window shows) rather than inferred from the
        // dependency graph. Null when this asset wasn't reimported because of another asset, or when
        // no previous revision was available to diff against.
        public string CausedByPath;
        public readonly List<string> ReasonMessages = new List<string>();

        public double ImportDurationMs => ImportDurationMicroseconds / 1000.0;

        public string DisplayName => string.IsNullOrEmpty(AssetPath)
            ? "(unknown asset)"
            : AssetPath.Substring(AssetPath.LastIndexOf('/') + 1);
    }
}
