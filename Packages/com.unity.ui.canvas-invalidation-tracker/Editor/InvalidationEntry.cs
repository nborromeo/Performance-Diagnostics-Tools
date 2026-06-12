using System;
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

        // Call-site stack trace captured at the moment of queue registration.
        // Null when the method detour could not be installed on this platform.
        public string StackTrace;

        // Structured per-frame data for clickable stack trace links.
        // Parallel to the lines in StackTrace; FilePath may be null for frames
        // without debug info (e.g. Unity internals compiled without symbols).
        public StackFrameInfo[] StackFrames;
    }

    public struct StackFrameInfo
    {
        public string DisplayLine; // e.g. "  at Foo.Bar()  [Foo.cs:42]"
        public string FilePath;    // full absolute path; null if unavailable
        public int    Line;
    }
}
