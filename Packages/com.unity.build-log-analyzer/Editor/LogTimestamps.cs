using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BuildLogAnalyzer.Editor
{
    /// <summary>
    /// Detects a consistent leading timestamp format across the parsed log and exposes the
    /// raw timestamp string for any given line. Detection samples the file and picks the
    /// pattern that matches the largest share of lines (above a threshold), so a log either
    /// has a confidently detected format or none — in which case timestamps are omitted
    /// rather than guessed. Raw substrings are kept for display to sidestep locale-parsing
    /// pitfalls; ISO-style timestamps also sort chronologically as plain text.
    /// </summary>
    static class LogTimestamps
    {
        sealed class Candidate
        {
            public readonly string Name;
            public readonly Regex  Rx;
            public Candidate(string name, string pattern)
            {
                Name = name;
                Rx   = new Regex(pattern, RegexOptions.Compiled);
            }
        }

        // Ordered most-specific / most-desirable first; equal match counts break toward the
        // earlier entry (see Detect). Each pattern captures the timestamp itself in group 1.
        static readonly Candidate[] s_Candidates =
        {
            new Candidate("ISO 8601",            @"^\s*(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:Z|[+-]\d{2}:?\d{2})?)"),
            new Candidate("Bracketed date-time", @"^\s*\[(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:Z|[+-]\d{2}:?\d{2})?)\]"),
            new Candidate("Bracketed time",      @"^\s*\[(\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)\]"),
            new Candidate("Syslog",              @"^\s*([A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})"),
            new Candidate("Time of day",         @"^\s*(\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)\b"),
            new Candidate("Elapsed seconds",     @"^\s*\[\s*(\d+(?:[.,]\d+)?)\s*s\s*\]"),
        };

        const int    k_SampleLimit = 1000;
        const double k_Threshold   = 0.5;
        const int    k_MinMatches  = 8;

        static string[] s_PerLine = Array.Empty<string>();

        public static bool   HasTimestamps      { get; private set; }
        public static string DetectedFormatName { get; private set; } = string.Empty;
        public static string SampleTimestamp    { get; private set; } = string.Empty;
        public static string FirstTimestamp     { get; private set; } = string.Empty;
        public static string LastTimestamp      { get; private set; } = string.Empty;

        public static void Clear()
        {
            s_PerLine          = Array.Empty<string>();
            HasTimestamps      = false;
            DetectedFormatName = string.Empty;
            SampleTimestamp    = string.Empty;
            FirstTimestamp     = string.Empty;
            LastTimestamp      = string.Empty;
        }

        /// <summary>Returns the timestamp for a 1-based line number, or empty if none.</summary>
        public static bool TryGet(int lineNumber, out string timestamp)
        {
            int idx = lineNumber - 1;
            if (idx >= 0 && idx < s_PerLine.Length && !string.IsNullOrEmpty(s_PerLine[idx]))
            {
                timestamp = s_PerLine[idx];
                return true;
            }
            timestamp = string.Empty;
            return false;
        }

        /// <summary>Convenience accessor for sort comparers; empty string when absent.</summary>
        public static string Get(int lineNumber) => TryGet(lineNumber, out string ts) ? ts : string.Empty;

        public static void Detect(string[] lines)
        {
            Clear();
            if (lines == null || lines.Length == 0) return;

            // Sample lines spread across the whole file. Build logs (e.g. TeamCity) often
            // begin with an un-timestamped preamble — environment dump, command line, package
            // resolution — before the timestamped body starts, so sampling only the head can
            // miss the format entirely. Striding across the file catches the body too.
            var sample = new List<string>(Math.Min(k_SampleLimit, lines.Length));
            int stride = Math.Max(1, lines.Length / k_SampleLimit);
            for (int i = 0; i < lines.Length && sample.Count < k_SampleLimit; i += stride)
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    sample.Add(lines[i]);
            if (sample.Count == 0) return;

            Candidate best        = null;
            int       bestMatches = 0;
            foreach (var c in s_Candidates)
            {
                int matches = 0;
                foreach (var s in sample)
                    if (c.Rx.IsMatch(s)) matches++;
                if (matches > bestMatches) { bestMatches = matches; best = c; }
            }

            if (best == null || bestMatches < k_MinMatches
                || (double)bestMatches / sample.Count < k_Threshold)
                return;

            // Extract the raw timestamp for every line under the chosen format.
            s_PerLine = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var m = best.Rx.Match(lines[i]);
                if (!m.Success) continue;

                string ts    = m.Groups[1].Value.Trim();
                s_PerLine[i] = ts;
                if (string.IsNullOrEmpty(FirstTimestamp)) FirstTimestamp = ts;
                LastTimestamp = ts;
            }

            HasTimestamps      = true;
            DetectedFormatName = best.Name;
            SampleTimestamp    = FirstTimestamp;
        }
    }
}
