using System;
using UnityEngine;

namespace CanvasInvalidationTracker
{
    public enum InvalidationType
    {
        Layout,
        Graphic
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

        // Call-site stack trace captured at the moment of queue registration.
        // Null when the method detour could not be installed on this platform.
        public string StackTrace;
    }
}
