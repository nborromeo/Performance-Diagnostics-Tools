using System.Collections.Generic;

namespace BuildLogAnalyzer.Editor
{
    readonly struct RowWarning
    {
        public readonly string Message;
        public RowWarning(string message) { Message = message; }
    }

    // Implement this interface (as a nested class inside a tab) to add warning checks
    // for any entry type. Register instances via the tab's warning analyzer list.
    interface IRowWarningAnalyzer<TEntry>
    {
        void Analyze(TEntry entry, List<RowWarning> results);
    }
}
