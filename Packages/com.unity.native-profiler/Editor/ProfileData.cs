using System;
using System.Collections.Generic;

namespace NativeProfiler
{
    internal enum FunctionKind
    {
        Native,
        Managed,
        Unknown,   // unsymbolicated address (JIT code we couldn't resolve, stripped binaries...)
        Synthetic, // thread roots, "[native only]" buckets, etc.
    }

    internal sealed class FunctionInfo
    {
        public string Name;
        public string Module;
        public FunctionKind Kind;
    }

    /// <summary>One unique frame (address) as reported by xctrace.</summary>
    internal sealed class RawFrame
    {
        public ulong Address;
        public string Name;
        public string Module;
        public string ModulePath;
        /// <summary>No owning binary and no symbol: most likely Mono JIT code.</summary>
        public bool IsJitCandidate;
        /// <summary>Function this frame maps to (set by the parser for native frames, by symbolication for JIT ones).</summary>
        public int FunctionId = -1;
    }

    internal sealed class ThreadInfo
    {
        public string Name;
        public long Tid;
        public long TotalWeightNs;
        public bool IsMain => Name != null && Name.StartsWith("Main Thread", StringComparison.Ordinal);
    }

    internal struct Sample
    {
        public int Thread;
        public long TimeNs;    // relative to trace start
        public long WeightNs;
        public int[] Frames;   // RawFrame indices, leaf first (same order as xctrace)
        public bool Stale;     // captured before the current scripting domain was loaded
    }

    internal sealed class ProfileData
    {
        public string CaptureDirectory;
        public string TracePath;
        public int TargetPid;
        public string TargetName;
        public DateTime StartUtc;
        public double DurationSeconds;

        public readonly List<FunctionInfo> Functions = new List<FunctionInfo>();
        public readonly List<RawFrame> Frames = new List<RawFrame>();
        public readonly List<ThreadInfo> Threads = new List<ThreadInfo>();
        public readonly List<Sample> Samples = new List<Sample>();

        public int JitFramesTotal;
        public int JitFramesResolved;
        public string SymbolicationNote;

        readonly Dictionary<(string, string, FunctionKind), int> m_FunctionLookup = new Dictionary<(string, string, FunctionKind), int>();

        public long MaxTimeNs
        {
            get
            {
                long max = 0;
                foreach (var s in Samples)
                    max = Math.Max(max, s.TimeNs + s.WeightNs);
                return max;
            }
        }

        public int InternFunction(string name, string module, FunctionKind kind)
        {
            var key = (name, module ?? string.Empty, kind);
            if (m_FunctionLookup.TryGetValue(key, out var id))
                return id;
            id = Functions.Count;
            Functions.Add(new FunctionInfo { Name = name, Module = module ?? string.Empty, Kind = kind });
            m_FunctionLookup.Add(key, id);
            return id;
        }
    }
}
