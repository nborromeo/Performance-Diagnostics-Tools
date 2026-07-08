using System;
using System.Collections.Generic;
using UnityEditor;

namespace BuildLogAnalyzer.Editor
{
    abstract class BuildLogAnalyzerTab
    {
        public abstract string TabName { get; }
        public virtual void OnEnable(EditorWindow window) { }
        public abstract void ParseLines(string[] lines);
        public abstract void DrawGUI(float contentWidth);
        public abstract void Clear();
        public virtual string GetStatusMessage() => string.Empty;

        /// <summary>Rows contributed to the Timeline tab's aggregated view. Empty by default.</summary>
        public virtual IEnumerable<SummaryRow> GetSummaryRows() => Array.Empty<SummaryRow>();

        /// <summary>Selects (and frames) the entry starting at <paramref name="lineNumber"/>, as requested from the Timeline tab.</summary>
        public virtual void SelectSummaryRow(int lineNumber) { }
    }
}
