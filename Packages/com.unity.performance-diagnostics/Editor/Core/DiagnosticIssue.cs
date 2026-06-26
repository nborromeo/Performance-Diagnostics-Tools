using UnityEngine;

namespace PerformanceDiagnostics
{
    /// <summary>
    /// Unified data transfer object used by the shared list view.
    /// Each detector maps its internal data to this type for display;
    /// the original internal data is preserved in <see cref="DetectorPayload"/>
    /// so the detector's details panel can render rich, type-specific information.
    /// </summary>
    public sealed class DiagnosticIssue
    {
        public int   Id;
        public int   Count = 1;
        public int   FrameNumber;
        public float Time;
        public bool  IsInPlayMode;

        // Detector identity
        public string Category;       // e.g. "Canvas Invalidation", "Static Rebuild"
        public Color  CategoryColor;  // color for the left edge strip in the list row

        // Issue classification
        public string IssueType;      // e.g. "LAYOUT", "GRAPHIC", "SetColor", "Transform"
        public Color  IssueTypeColor; // badge color; falls back to CategoryColor when default

        // Object
        public string     ObjectName;
        public string     HierarchyPath;
        public GameObject Target;

        // Detector-specific context shown in the "Context" column
        // (e.g. Canvas name for UI issues, empty for physics issues)
        public string ContextName;

        // Detector casts this back to its own internal type inside DrawIssueDetails.
        public object DetectorPayload;
    }
}
