using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HU.NativeProfiler
{
    /// <summary>
    /// Turns the unsymbolicated (JIT) frames of a capture into managed method names, either live
    /// through <see cref="MonoSymbolResolver"/> (capture of this Editor, same domain) or from the
    /// symbols.json cache written the first time a capture was resolved.
    /// </summary>
    internal static class Symbolicator
    {
        const string k_CacheFile = "symbols.json";

        [Serializable]
        class SymbolCache
        {
            public long staleBeforeNs;
            public List<Entry> entries = new List<Entry>();
        }

        [Serializable]
        class Entry
        {
            public string address;
            public string name;
            public string module;
        }

        public static int StaleFunctionId(ProfileData data) =>
            data.InternFunction("[JIT code unloaded by domain reload]", "managed", FunctionKind.Unknown);

        public static void Symbolicate(ProfileData data, DateTime domainLoadedUtc)
        {
            var cachePath = Path.Combine(data.CaptureDirectory, k_CacheFile);
            var jitFrames = new List<RawFrame>();
            foreach (var f in data.Frames)
                if (f.IsJitCandidate)
                    jitFrames.Add(f);
            data.JitFramesTotal = jitFrames.Count;

            long staleBeforeNs = 0;
            if (File.Exists(cachePath))
            {
                var cache = JsonUtility.FromJson<SymbolCache>(File.ReadAllText(cachePath));
                var byAddress = new Dictionary<string, Entry>();
                foreach (var e in cache.entries)
                    byAddress[e.address] = e;
                foreach (var f in jitFrames)
                    if (byAddress.TryGetValue(f.Address.ToString("x"), out var e))
                        f.FunctionId = data.InternFunction(e.name, e.module, FunctionKind.Managed);
                staleBeforeNs = cache.staleBeforeNs;
                data.SymbolicationNote = "Managed frames resolved from symbols cache.";
            }
            else if (CanResolveLive(data, out var reason))
            {
                // JIT addresses sampled before this domain existed point to freed code: never resolve those.
                if (data.StartUtc != default && domainLoadedUtc > data.StartUtc)
                    staleBeforeNs = (long)((domainLoadedUtc - data.StartUtc).TotalMilliseconds * 1e6);

                var cache = new SymbolCache { staleBeforeNs = staleBeforeNs };
                foreach (var f in jitFrames)
                {
                    if (!MonoSymbolResolver.TryResolve(f.Address, out var name, out var module))
                        continue;
                    f.FunctionId = data.InternFunction(name, module, FunctionKind.Managed);
                    cache.entries.Add(new Entry { address = f.Address.ToString("x"), name = name, module = module });
                }
                File.WriteAllText(cachePath, JsonUtility.ToJson(cache));
                data.SymbolicationNote = staleBeforeNs > 0
                    ? $"A domain reload happened {staleBeforeNs / 1e9:0.00}s into the capture: JIT frames before that point can't be resolved."
                    : "Managed frames resolved live from the Mono runtime.";
            }
            else
            {
                data.SymbolicationNote = reason;
            }

            foreach (var f in jitFrames)
            {
                if (f.FunctionId >= 0)
                    data.JitFramesResolved++;
                else
                    f.FunctionId = data.InternFunction(f.Name, "JIT?", FunctionKind.Unknown);
            }

            if (staleBeforeNs > 0)
            {
                for (var i = 0; i < data.Samples.Count; i++)
                {
                    var s = data.Samples[i];
                    if (s.TimeNs < staleBeforeNs)
                    {
                        s.Stale = true;
                        data.Samples[i] = s;
                    }
                }
            }
        }

        static bool CanResolveLive(ProfileData data, out string reason)
        {
            reason = null;
            if (data.TargetPid != System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                reason = "Capture is from another process: managed (Mono JIT) frames can only be resolved for captures of this Editor. IL2CPP builds are fully symbolicated by xctrace.";
                return false;
            }
            if (!MonoSymbolResolver.IsAvailable)
            {
                reason = "Mono runtime symbols not found: managed frames left unresolved.";
                return false;
            }
            return true;
        }
    }
}
