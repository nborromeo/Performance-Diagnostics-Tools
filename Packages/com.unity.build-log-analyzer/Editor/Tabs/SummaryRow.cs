namespace BuildLogAnalyzer.Editor
{
    /// <summary>A single entry contributed by a tab to the Timeline tab's aggregated view.</summary>
    sealed class SummaryRow
    {
        public int                 LineNumber;
        public int                 LineNumberEnd;
        public string              Name;
        public float               DurationSec;
        public string              Category;
        public BuildLogAnalyzerTab SourceTab;
        public int                 TabIndex;
    }
}
