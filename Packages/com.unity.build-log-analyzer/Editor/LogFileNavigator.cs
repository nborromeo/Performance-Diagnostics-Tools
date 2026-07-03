using System;
using System.Diagnostics;
using System.IO;
using Unity.CodeEditor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BuildLogAnalyzer.Editor
{
    /// <summary>
    /// Shared helper for the analyzer tabs: remembers the path of the log file that
    /// was last parsed, opens it at a given line in Unity's configured External Script
    /// Editor (VS Code, Rider, etc. — works on both Windows and macOS), and renders the
    /// "Line" column with independently clickable start/end line numbers.
    /// </summary>
    static class LogFileNavigator
    {
        /// <summary>Absolute path of the log file that was last parsed, or empty if none.</summary>
        public static string LogFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Opens the parsed log file at <paramref name="line"/> (1-based) in the editor set
        /// under Preferences ▸ External Tools ▸ External Script Editor.
        /// </summary>
        /// <remarks>
        /// We launch the editor executable directly rather than going through the CodeEditor
        /// integration: Rider (and others) reject non-source files like ".log" from
        /// <c>IExternalCodeEditor.OpenProject</c>, causing Unity to silently fall back to the
        /// OS file association — which opens the wrong editor and ignores the line.
        /// </remarks>
        public static void OpenAtLine(int line)
        {
            if (string.IsNullOrEmpty(LogFilePath) || !File.Exists(LogFilePath))
            {
                Debug.LogWarning($"[Build Log Analyzer] Cannot open log file — path unavailable: \"{LogFilePath}\"");
                return;
            }

            line = Mathf.Max(1, line);

            string editorPath = CodeEditor.CurrentEditorInstallation?.Trim() ?? string.Empty;
            if (TryOpenInExternalEditor(editorPath, LogFilePath, line))
                return;

            // Unknown/unsupported editor — open with the OS default (no line jump).
            Debug.LogWarning($"[Build Log Analyzer] Couldn't open the configured editor at a specific line " +
                             $"(\"{editorPath}\"); opening with the OS default application instead.");
            EditorUtility.OpenWithDefaultApp(LogFilePath);
        }

        // Launches the given editor with the correct command-line arguments to jump to a line.
        // Returns false for editors we don't recognise so the caller can fall back.
        static bool TryOpenInExternalEditor(string editorPath, string file, int line)
        {
            if (string.IsNullOrEmpty(editorPath)) return false;

            string name  = editorPath.ToLowerInvariant();
            bool   isMac = Application.platform == RuntimePlatform.OSXEditor;
            bool   isApp = isMac && editorPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

            string exe = editorPath;
            string args;

            if (name.Contains("rider"))
            {
                if (isApp) exe = Path.Combine(editorPath, "Contents/MacOS/rider");
                args = $"--line {line} \"{file}\"";
            }
            else if (name.Contains("code") || name.Contains("cursor") || name.Contains("vscodium"))
            {
                // VS Code / Cursor / VSCodium: --goto <file>:<line>[:<col>]
                if (isApp) exe = Path.Combine(editorPath, "Contents/Resources/app/bin/code");
                args = $"--goto \"{file}:{line}\"";
            }
            else if (name.Contains("sublime") || name.Contains("subl"))
            {
                if (isApp) exe = Path.Combine(editorPath, "Contents/SharedSupport/bin/subl");
                args = $"\"{file}:{line}\"";
            }
            else if (name.Contains("notepad++"))
            {
                args = $"-n{line} \"{file}\"";
            }
            else
            {
                return false; // unrecognised editor
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    Arguments       = args,
                    UseShellExecute = false,
                });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Build Log Analyzer] Failed to launch editor \"{exe} {args}\": {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Draws a "Line" cell in which the start line number — and the end line number
        /// when the entry spans a range — are separately clickable links that open the
        /// log at that line. <paramref name="end"/> ≤ <paramref name="start"/> renders
        /// just the start number.
        /// </summary>
        public static void DrawLineCell(Rect rect, int start, int end)
        {
            var linkStyle = EditorStyles.linkLabel;

            var startContent = new GUIContent(start.ToString(), $"Open log at line {start}");
            float startW     = linkStyle.CalcSize(startContent).x;
            var   startRect  = new Rect(rect.x, rect.y, startW, rect.height);
            EditorGUIUtility.AddCursorRect(startRect, MouseCursor.Link);
            if (GUI.Button(startRect, startContent, linkStyle))
                OpenAtLine(start);

            if (end <= start) return;

            var   sepContent = new GUIContent("–");
            float sepW       = EditorStyles.label.CalcSize(sepContent).x;
            var   sepRect    = new Rect(startRect.xMax, rect.y, sepW, rect.height);
            GUI.Label(sepRect, sepContent);

            var endContent = new GUIContent(end.ToString(), $"Open log at line {end}");
            float endW     = linkStyle.CalcSize(endContent).x;
            var   endRect  = new Rect(sepRect.xMax, rect.y, endW, rect.height);
            EditorGUIUtility.AddCursorRect(endRect, MouseCursor.Link);
            if (GUI.Button(endRect, endContent, linkStyle))
                OpenAtLine(end);
        }

        /// <summary>
        /// Draws the "Timestamp" cell for an entry. Shows the timestamp of the start line;
        /// when the entry spans a range with a differing end timestamp, the tooltip shows
        /// "start → end". Empty when the log has no detected timestamps.
        /// </summary>
        public static void DrawTimestampCell(Rect rect, int startLine, int endLine)
        {
            LogTimestamps.TryGet(startLine, out string startTs);
            string tooltip = startTs;
            if (endLine > startLine && LogTimestamps.TryGet(endLine, out string endTs) && endTs != startTs)
                tooltip = $"{startTs}  →  {endTs}";
            EditorGUI.LabelField(rect, new GUIContent(startTs, tooltip));
        }
    }
}
