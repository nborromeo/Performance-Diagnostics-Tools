using System;
using System.Collections.Generic;
using UnityEngine;

namespace PerformanceDiagnostics.Detectors
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
        public GameObject Target;
        public string[]   ComponentTypeNames;

        public string CanvasName;
        public string CanvasRenderMode;

        public InvalidationType  Type;
        public GraphicDirtyFlags DirtyFlags;

        public string MethodName;  // populated for CanvasRenderer entries
        public bool   NativeOnly;  // true for CanvasRenderer entries

        public readonly List<(string trace, StackFrameInfo[] frames)> Traces =
            new List<(string, StackFrameInfo[])>();

        public string           StackTrace  => Traces.Count > 0 ? Traces[0].trace  : null;
        public StackFrameInfo[] StackFrames => Traces.Count > 0 ? Traces[0].frames : null;
    }

    public struct StackFrameInfo
    {
        public string DisplayLine;
        public string FilePath;
        public int    Line;
    }
}
