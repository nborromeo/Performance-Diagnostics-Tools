using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

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

        /// <summary>
        /// Builds the "Showing N of M &lt;noun&gt;  |  extra stats" status message shared by every tab's
        /// filter balloon. <paramref name="extra"/> should already be computed from the filtered set so
        /// the displayed totals track the active filter, not the full unfiltered log.
        /// </summary>
        protected static string BuildStatusMessage(int shown, int total, string noun, string extra = "")
        {
            return shown == total
                ? $"Showing {total} {noun}{extra}"
                : $"Showing {shown} of {total} {noun}{extra}";
        }

        protected static string FormatDuration(float seconds)
        {
            int totalSec = Mathf.FloorToInt(seconds);
            if (totalSec < 60) return $"{seconds:F3}s";
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            return h > 0 ? $"{h}h {m}m {s}s" : $"{m}m {s}s";
        }
    }
}
