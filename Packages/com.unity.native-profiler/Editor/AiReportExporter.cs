using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NativeProfiler
{
    /// <summary>
    /// Writes the current view as a self-describing Markdown report meant to be handed to an LLM:
    /// capture context, per-thread cost, CPU over time, flat hotspots and pruned top-down / bottom-up trees.
    /// Everything is pre-aggregated and pruned so the report fits comfortably in a model's context window.
    /// </summary>
    internal static class AiReportExporter
    {
        const int k_TimelineBins = 50;
        const int k_TopSelf = 50;
        const int k_TopManaged = 30;
        const int k_TopDownMaxLines = 400;
        const double k_TopDownMinShare = 0.01;   // of the view's total time
        const int k_Hotspots = 15;
        const int k_CallerDepth = 15;
        const int k_CallerMaxLines = 25;
        const double k_CallerMinShare = 0.05;    // of the hotspot's self time

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        sealed class FunctionStats
        {
            public int FunctionId;
            public long SelfNs;
            public long InclusiveNs;
        }

        public static string Build(ProfileData data, CallTreeOptions options, string threadLabel)
        {
            options.Inverted = false;
            var tree = CallTree.Build(data, options);
            var total = Math.Max(1, tree.TotalNs);
            // Without a selection, start at the first sample: xctrace takes a moment to attach.
            var rangeMin = options.MaxTimeNs > 0 || data.Samples.Count == 0 ? options.MinTimeNs : data.Samples.Min(s => s.TimeNs);
            var rangeMax = options.MaxTimeNs > 0 ? options.MaxTimeNs : data.MaxTimeNs;
            var rangeNs = Math.Max(1, rangeMax - rangeMin);

            var sb = new StringBuilder();
            WriteHeader(sb, data, options, threadLabel, tree, rangeMin, rangeMax);
            WriteThreads(sb, data, options, rangeNs);
            WriteTimeline(sb, data, options, rangeMin, rangeNs);

            var stats = ComputeFunctionStats(data, options);
            WriteHotspots(sb, data, stats, total);
            WriteManaged(sb, data, stats, total);
            WriteTopDown(sb, data, tree, total);
            WriteCallers(sb, data, options, stats, total);
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------

        static void WriteHeader(StringBuilder sb, ProfileData data, CallTreeOptions o, string threadLabel, CallTree tree, long rangeMin, long rangeMax)
        {
            sb.Append("# Native CPU profile report\n\n");
            sb.Append("This is a sampled CPU profile of a Unity process, recorded with Apple's xctrace (Instruments Time Profiler) and exported by the ")
              .Append("Unity Native CPU Profiler tool. Each sample is a full call stack (native engine/system frames plus managed C# frames resolved ")
              .Append("from Mono JIT code). Times are aggregated sample weights, so they approximate CPU time actually spent on each thread.\n\n");
            sb.Append("**Suggested analysis:** identify the main CPU bottlenecks, explain which Unity systems or user scripts they belong to, ")
              .Append("point out spikes in the timeline, and propose concrete optimizations.\n\n");

            sb.Append("## Capture\n\n");
            Kv(sb, "Target process", $"{data.TargetName} (pid {data.TargetPid})");
            Kv(sb, "Recorded (UTC)", data.StartUtc == default ? "unknown" : data.StartUtc.ToString("yyyy-MM-dd HH:mm:ss", k_Inv));
            Kv(sb, "Capture length", $"{F(data.MaxTimeNs / 1e9, "0.000")} s");
            Kv(sb, "Total samples (all threads)", data.Samples.Count.ToString(k_Inv));
            Kv(sb, "Threads", data.Threads.Count.ToString(k_Inv));
            if (data.JitFramesTotal > 0)
                Kv(sb, "Managed (JIT) frames resolved", $"{data.JitFramesResolved}/{data.JitFramesTotal}");
            if (!string.IsNullOrEmpty(data.SymbolicationNote))
                Kv(sb, "Symbolication", data.SymbolicationNote);
            sb.Append('\n');

            sb.Append("## Current view (filters applied to every section below unless stated)\n\n");
            Kv(sb, "Thread", threadLabel);
            Kv(sb, "Time range", o.MaxTimeNs > 0 ? $"{F(rangeMin / 1e9, "0.000")} s – {F(rangeMax / 1e9, "0.000")} s (user selection)" : $"whole capture (samples from {F(rangeMin / 1e9, "0.000")} s to {F(rangeMax / 1e9, "0.000")} s)");
            Kv(sb, "Managed only", o.ManagedOnly ? "yes — native frames hidden, except the first native callee below managed code" : "no");
            Kv(sb, "Samples in view", tree.SampleCount.ToString(k_Inv));
            Kv(sb, "CPU time in view", $"{Ms(tree.TotalNs)} ms");
            sb.Append('\n');

            sb.Append("## Legend\n\n");
            sb.Append("- **Self**: time spent in the function's own code (it was the leaf frame of the sample).\n");
            sb.Append("- **Inclusive / Total**: time spent in the function and everything it called. Recursion is counted once per sample.\n");
            sb.Append("- **%**: share of the CPU time in view.\n");
            sb.Append("- **Kind**: `native` (engine/system code), `managed` (C# method, `Namespace.Type:Method`), `unknown` (unsymbolicated address), `synthetic` (grouping node added by the tool, e.g. thread roots).\n");
            sb.Append("- Tree lines read `total ms (total %) [self ms] function {module}`. Indentation is call depth. ")
              .Append("Chains of frames that pass all their time to a single callee are collapsed into one line joined with ` > `.\n\n");
        }

        static void WriteThreads(StringBuilder sb, ProfileData data, CallTreeOptions o, long rangeNs)
        {
            var perThread = new long[data.Threads.Count];
            var all = o;
            all.Thread = -1;
            foreach (var s in data.Samples)
                if (CallTree.PassesFilter(s, all))
                    perThread[s.Thread] += s.WeightNs;

            sb.Append("## Threads (time range applied, thread filter ignored)\n\n");
            sb.Append("| Thread | CPU ms | Busy % of wall time |\n|---|---:|---:|\n");
            foreach (var i in Enumerable.Range(0, perThread.Length).OrderByDescending(i => perThread[i]))
            {
                if (perThread[i] == 0)
                    continue;
                var marker = i == o.Thread ? " **(in view)**" : "";
                sb.Append("| ").Append(Cell(data.Threads[i].Name)).Append(marker)
                  .Append(" | ").Append(Ms(perThread[i]))
                  .Append(" | ").Append(F(100.0 * perThread[i] / rangeNs, "0.0")).Append(" |\n");
            }
            sb.Append('\n');
        }

        static void WriteTimeline(StringBuilder sb, ProfileData data, CallTreeOptions o, long rangeMin, long rangeNs)
        {
            var bins = new long[k_TimelineBins];
            var binNs = rangeNs / (double)k_TimelineBins;
            foreach (var s in data.Samples)
            {
                if (!CallTree.PassesFilter(s, o))
                    continue;
                var b = (int)((s.TimeNs - rangeMin) / binNs);
                bins[Math.Max(0, Math.Min(k_TimelineBins - 1, b))] += s.WeightNs;
            }

            var mean = bins.Average();
            var std = Math.Sqrt(bins.Sum(b => (b - mean) * (b - mean)) / bins.Length);
            sb.Append("## CPU over time (view filters applied)\n\n");
            sb.Append($"{k_TimelineBins} equal buckets of {F(binNs / 1e6, "0.##")} ms. Mean {Ms((long)mean)} ms per bucket, std dev {Ms((long)std)} ms. ")
              .Append("Buckets above mean + 2σ are flagged as spikes. Busy % can exceed 100 when several threads are in view.\n\n");
            sb.Append("| Start (s) | CPU ms | Busy % | |\n|---:|---:|---:|---|\n");
            for (var i = 0; i < bins.Length; i++)
            {
                var spike = std > 0 && bins[i] > mean + 2 * std ? "spike" : "";
                sb.Append("| ").Append(F((rangeMin + i * binNs) / 1e9, "0.000"))
                  .Append(" | ").Append(Ms(bins[i]))
                  .Append(" | ").Append(F(100.0 * bins[i] / binNs, "0.0"))
                  .Append(" | ").Append(spike).Append(" |\n");
            }
            sb.Append('\n');
        }

        static List<FunctionStats> ComputeFunctionStats(ProfileData data, CallTreeOptions o)
        {
            var staleId = Symbolicator.StaleFunctionId(data);
            var nativeOnlyId = CallTree.NativeOnlyFunctionId(data);
            var byId = new Dictionary<int, FunctionStats>();
            var stack = new List<int>(256);
            var seen = new HashSet<int>();

            FunctionStats Get(int id)
            {
                if (!byId.TryGetValue(id, out var st))
                    byId[id] = st = new FunctionStats { FunctionId = id };
                return st;
            }

            foreach (var s in data.Samples)
            {
                if (!CallTree.PassesFilter(s, o))
                    continue;
                CallTree.FillStack(data, s, o.ManagedOnly, stack, staleId, nativeOnlyId);
                if (stack.Count == 0)
                    continue;
                Get(stack[stack.Count - 1]).SelfNs += s.WeightNs;
                seen.Clear();
                foreach (var id in stack)
                    if (seen.Add(id))
                        Get(id).InclusiveNs += s.WeightNs;
            }
            return byId.Values.ToList();
        }

        static void WriteHotspots(StringBuilder sb, ProfileData data, List<FunctionStats> stats, long total)
        {
            sb.Append($"## Top {k_TopSelf} functions by self time\n\n");
            sb.Append("Where the CPU is actually burning. Look at the callers section further down to see who drives each one.\n\n");
            WriteFunctionTable(sb, data, stats.Where(s => s.SelfNs > 0).OrderByDescending(s => s.SelfNs).Take(k_TopSelf), total);
        }

        static void WriteManaged(StringBuilder sb, ProfileData data, List<FunctionStats> stats, long total)
        {
            var managed = stats.Where(s => data.Functions[s.FunctionId].Kind == FunctionKind.Managed)
                .OrderByDescending(s => s.InclusiveNs).Take(k_TopManaged).ToList();
            if (managed.Count == 0)
                return;
            sb.Append($"## Top {k_TopManaged} managed (C#) methods by inclusive time\n\n");
            sb.Append("Cost of script code including everything it calls into (engine APIs, allocations, etc.).\n\n");
            WriteFunctionTable(sb, data, managed, total);
        }

        static void WriteFunctionTable(StringBuilder sb, ProfileData data, IEnumerable<FunctionStats> rows, long total)
        {
            sb.Append("| # | Self ms | Self % | Incl. ms | Incl. % | Kind | Function | Module |\n|---:|---:|---:|---:|---:|---|---|---|\n");
            var rank = 1;
            foreach (var s in rows)
            {
                var fn = data.Functions[s.FunctionId];
                sb.Append("| ").Append(rank++)
                  .Append(" | ").Append(Ms(s.SelfNs)).Append(" | ").Append(Pct(s.SelfNs, total))
                  .Append(" | ").Append(Ms(s.InclusiveNs)).Append(" | ").Append(Pct(s.InclusiveNs, total))
                  .Append(" | ").Append(Kind(fn.Kind))
                  .Append(" | `").Append(Cell(fn.Name).Replace("`", "'")).Append('`')
                  .Append(" | ").Append(Cell(fn.Module)).Append(" |\n");
            }
            sb.Append('\n');
        }

        static void WriteTopDown(StringBuilder sb, ProfileData data, CallTree tree, long total)
        {
            var minNs = (long)(total * k_TopDownMinShare);
            sb.Append("## Top-down call tree\n\n");
            sb.Append($"From thread entry points down to leaves. Branches under {F(k_TopDownMinShare * 100, "0.#")}% of the view are pruned")
              .Append($" (limited to {k_TopDownMaxLines} lines).\n\n```text\n");
            var lines = 0;
            if (tree.Root.HasChildren)
                foreach (var c in tree.Root.Children)
                    WriteTreeNode(sb, data, c, 0, total, minNs, k_TopDownMaxLines, ref lines);
            if (lines >= k_TopDownMaxLines)
                sb.Append("… (truncated)\n");
            sb.Append("```\n\n");
        }

        static void WriteCallers(StringBuilder sb, ProfileData data, CallTreeOptions o, List<FunctionStats> stats, long total)
        {
            var hotspots = stats.Where(s => s.SelfNs > 0).OrderByDescending(s => s.SelfNs).Take(k_Hotspots).ToList();
            if (hotspots.Count == 0)
                return;

            // Bottom-up tree per hotspot: the hotspot is the root, its callers are the children.
            var roots = hotspots.ToDictionary(h => h.FunctionId, h => new CallNode { FunctionId = h.FunctionId });
            var registry = new List<CallNode>();
            var staleId = Symbolicator.StaleFunctionId(data);
            var nativeOnlyId = CallTree.NativeOnlyFunctionId(data);
            var stack = new List<int>(256);
            foreach (var s in data.Samples)
            {
                if (!CallTree.PassesFilter(s, o))
                    continue;
                CallTree.FillStack(data, s, o.ManagedOnly, stack, staleId, nativeOnlyId);
                if (stack.Count == 0 || !roots.TryGetValue(stack[stack.Count - 1], out var node))
                    continue;
                node.TotalNs += s.WeightNs;
                node.SelfNs += s.WeightNs;
                for (var i = stack.Count - 2; i >= 0 && stack.Count - 1 - i <= k_CallerDepth; i--)
                {
                    node = node.GetOrAddChild(stack[i], registry);
                    node.TotalNs += s.WeightNs;
                }
            }

            sb.Append($"## Callers of the top {hotspots.Count} hotspots (bottom-up)\n\n");
            sb.Append($"Each block starts at a hotspot and walks up its callers (up to {k_CallerDepth} levels, paths under ")
              .Append($"{F(k_CallerMinShare * 100, "0")}% of the hotspot's self time pruned). Percentages are of the whole view.\n\n");
            foreach (var h in hotspots)
            {
                var root = roots[h.FunctionId];
                root.SortRecursive();
                sb.Append("### ").Append(Cell(data.Functions[h.FunctionId].Name)).Append("\n\n```text\n");
                var lines = 0;
                WriteTreeNode(sb, data, root, 0, total, (long)(root.TotalNs * k_CallerMinShare), k_CallerMaxLines, ref lines);
                sb.Append("```\n\n");
            }
        }

        // -------------------------------------------------------------------------------------------

        static void WriteTreeNode(StringBuilder sb, ProfileData data, CallNode node, int depth, long total, long minNs, int maxLines, ref int lines)
        {
            if (lines >= maxLines || node.TotalNs < minNs)
                return;

            // Collapse pass-through chains: no self time and a single child carrying everything.
            var chain = new List<CallNode> { node };
            var tail = node;
            while (tail.SelfNs == 0 && tail.Children != null && tail.Children.Count == 1 && tail.Children[0].TotalNs == tail.TotalNs)
            {
                tail = tail.Children[0];
                chain.Add(tail);
            }

            sb.Append(' ', depth * 2)
              .Append(Ms(node.TotalNs)).Append(" ms (").Append(Pct(node.TotalNs, total)).Append(")");
            if (tail.SelfNs > 0)
                sb.Append(" [self ").Append(Ms(tail.SelfNs)).Append(']');
            sb.Append(' ');
            for (var i = 0; i < chain.Count; i++)
            {
                if (i > 0)
                    sb.Append(" > ");
                var fn = data.Functions[chain[i].FunctionId];
                sb.Append(fn.Name);
                if (!string.IsNullOrEmpty(fn.Module) && fn.Kind != FunctionKind.Synthetic)
                    sb.Append(" {").Append(fn.Module).Append('}');
            }
            sb.Append('\n');
            lines++;

            if (!tail.HasChildren)
                return;
            var hidden = 0L;
            foreach (var c in tail.Children)
            {
                if (lines >= maxLines || c.TotalNs < minNs)
                    hidden += c.TotalNs;
                else
                    WriteTreeNode(sb, data, c, depth + 1, total, minNs, maxLines, ref lines);
            }
            if (hidden > 0 && lines < maxLines)
            {
                sb.Append(' ', (depth + 1) * 2).Append(Ms(hidden)).Append(" ms (").Append(Pct(hidden, total)).Append(") … other callees/callers below threshold\n");
                lines++;
            }
        }

        static void Kv(StringBuilder sb, string key, string value) => sb.Append("- **").Append(key).Append(":** ").Append(value).Append('\n');
        static string F(double v, string format) => v.ToString(format, k_Inv);
        static string Ms(long ns) => F(ns / 1e6, "0.00");
        static string Pct(long ns, long total) => F(100.0 * ns / total, "0.0") + "%";
        static string Cell(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");

        static string Kind(FunctionKind k) => k switch
        {
            FunctionKind.Managed => "managed",
            FunctionKind.Native => "native",
            FunctionKind.Synthetic => "synthetic",
            _ => "unknown",
        };
    }
}
