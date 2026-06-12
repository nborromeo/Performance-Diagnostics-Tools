using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanvasInvalidationTracker
{
    public enum InvalidationType
    {
        Layout,
        Graphic,
        CanvasRenderer
    }

    [Flags]
    public enum GraphicDirtyFlags
    {
        None     = 0,
        Vertices = 1,
        Material = 2
    }

    public sealed class InvalidationEntry
    {
        public int    Id;
        public int    Count = 1;
        public int    FrameNumber;
        public float  Time;
        public bool   IsInPlayMode;

        public string     ObjectName;
        public string     HierarchyPath;
        public GameObject Target;          // direct reference; Unity fake-null if destroyed
        public string[]   ComponentTypeNames;

        public string CanvasName;
        public string CanvasRenderMode;

        public InvalidationType  Type;
        public GraphicDirtyFlags DirtyFlags;

        // Populated for InvalidationType.CanvasRenderer entries — the specific
        // CanvasRenderer method that was called (e.g. "SetColor", "SetMesh").
        public string MethodName;

        // True for CanvasRenderer entries — the invalidation bypassed the managed
        // CanvasUpdateRegistry and went directly through native CanvasRenderer setters.
        public bool NativeOnly;

        // All unique call-site stack traces observed for this entry.
        // Multiple traces accumulate when the same GO+canvas+type is invalidated
        // from different call sites across frames.
        public readonly List<(string trace, StackFrameInfo[] frames)> Traces =
            new List<(string, StackFrameInfo[])>();

        // Shortcuts to the first trace — kept for convenience.
        public string           StackTrace  => Traces.Count > 0 ? Traces[0].trace  : null;
        public StackFrameInfo[] StackFrames => Traces.Count > 0 ? Traces[0].frames : null;
    }

    public struct StackFrameInfo
    {
        public string DisplayLine; // e.g. "  at Foo.Bar()  [Foo.cs:42]"
        public string FilePath;    // full absolute path; null if unavailable
        public int    Line;
    }
}
