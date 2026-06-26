using System;
using System.Collections.Generic;
using UnityEngine;

namespace PerformanceDiagnostics
{
    /// <summary>
    /// Contract for a performance issue detector.
    ///
    /// Implement this interface and call <see cref="DiagnosticRegistry.Register"/> in an
    /// <c>[InitializeOnLoad]</c> static constructor to plug a new detector into the
    /// unified Performance Diagnostics window.
    ///
    /// The window handles the shared list, filtering, and common details header.
    /// Each detector is responsible for:
    ///   - Populating <see cref="Issues"/> and firing <see cref="Changed"/>
    ///   - Drawing its primary action control in the toolbar via <see cref="DrawToolbarControls"/>
    ///   - Optionally drawing secondary settings in a popup via <see cref="DrawSettingsGUI"/>
    ///   - Drawing its issue details via <see cref="DrawIssueDetails"/>
    /// </summary>
    public interface IDiagnosticDetector
    {
        /// <summary>Human-readable name shown as the category toggle label.</summary>
        string Category { get; }

        /// <summary>Color used for the left-edge strip in every list row from this detector.</summary>
        Color CategoryColor { get; }

        /// <summary>When false the detector's issues are hidden from the list.</summary>
        bool IsEnabled { get; set; }

        /// <summary>Live issue list — the window reads this directly.</summary>
        IReadOnlyList<DiagnosticIssue> Issues { get; }

        /// <summary>Fires whenever <see cref="Issues"/> changes (new issues, clear, etc.).</summary>
        event Action Changed;

        /// <summary>Clears all recorded issues.</summary>
        void Clear();

        /// <summary>
        /// Draws the primary action control(s) inline in the shared toolbar IMGUI row.
        /// Keep this to 1–2 controls (e.g. Pause/Resume, Capture/Stop, status dot).
        /// Called while GUILayout.BeginHorizontal(EditorStyles.toolbar) is active.
        /// </summary>
        void DrawToolbarControls();

        /// <summary>
        /// Whether this detector has secondary settings to expose via the ⚙ popup button.
        /// Default: false (no button shown).
        /// </summary>
        bool HasSettings => false;

        /// <summary>Size of the settings popup window. Only used when <see cref="HasSettings"/> is true.</summary>
        Vector2 SettingsPopupSize => new Vector2(220f, 60f);

        /// <summary>
        /// Draws the settings popup content. Only called when <see cref="HasSettings"/> is true
        /// and the user has opened the ⚙ popup.
        /// </summary>
        void DrawSettingsGUI() { }

        /// <summary>
        /// Draws the detector-specific portion of the details panel.
        /// Called inside a GUILayout.BeginScrollView / EndScrollView block.
        /// </summary>
        void DrawIssueDetails(DiagnosticIssue issue, ref Vector2 scroll, ref int traceIndex, ref Vector2 traceScroll);
    }
}
