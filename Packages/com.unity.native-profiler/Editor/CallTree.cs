using System.Collections.Generic;

namespace HU.NativeProfiler
{
    internal sealed class CallNode
    {
        public int Id;
        public int FunctionId;
        public long TotalNs;
        public long SelfNs;
        public CallNode Parent;
        public List<CallNode> Children;
        Dictionary<int, CallNode> m_ChildMap;

        public bool HasChildren => Children != null && Children.Count > 0;

        public CallNode GetOrAddChild(int functionId, List<CallNode> registry)
        {
            if (m_ChildMap == null)
            {
                m_ChildMap = new Dictionary<int, CallNode>();
                Children = new List<CallNode>();
            }
            if (m_ChildMap.TryGetValue(functionId, out var child))
                return child;
            child = new CallNode { Id = registry.Count + 1, FunctionId = functionId, Parent = this };
            registry.Add(child);
            m_ChildMap.Add(functionId, child);
            Children.Add(child);
            return child;
        }

        public void SortRecursive()
        {
            if (Children == null)
                return;
            Children.Sort((a, b) => b.TotalNs.CompareTo(a.TotalNs));
            foreach (var c in Children)
                c.SortRecursive();
        }
    }

    internal struct CallTreeOptions
    {
        public bool Inverted;     // bottom-up: roots are the functions where time is actually spent
        public bool ManagedOnly;  // drop native frames, keeping the native callee right below managed code
        public int Thread;        // -1 = all threads (one root per thread)
        public long MinTimeNs;
        public long MaxTimeNs;    // <= 0 = no upper bound
    }

    internal sealed class CallTree
    {
        public CallNode Root;
        public readonly List<CallNode> Nodes = new List<CallNode>(); // Nodes[id - 1]
        public long TotalNs;
        public int SampleCount;

        public CallNode Find(int id) => id >= 1 && id <= Nodes.Count ? Nodes[id - 1] : null;

        public static bool PassesFilter(in Sample s, in CallTreeOptions o) =>
            (o.Thread < 0 || s.Thread == o.Thread) && s.TimeNs >= o.MinTimeNs && (o.MaxTimeNs <= 0 || s.TimeNs < o.MaxTimeNs);

        public static CallTree Build(ProfileData data, CallTreeOptions options)
        {
            var tree = new CallTree { Root = new CallNode { Id = 0, FunctionId = -1 } };
            var staleId = Symbolicator.StaleFunctionId(data);
            var nativeOnlyId = data.InternFunction("[native only — no managed frames]", null, FunctionKind.Synthetic);
            var threadRoots = new Dictionary<int, int>();
            var stack = new List<int>(256);

            foreach (var s in data.Samples)
            {
                if (!PassesFilter(s, options))
                    continue;

                // Root -> leaf list of function ids.
                stack.Clear();
                for (var i = s.Frames.Length - 1; i >= 0; i--)
                {
                    var f = data.Frames[s.Frames[i]];
                    stack.Add(s.Stale && f.IsJitCandidate ? staleId : f.FunctionId);
                }
                if (options.ManagedOnly)
                    KeepManaged(data, stack, nativeOnlyId);
                if (options.Inverted)
                    stack.Reverse();
                if (options.Thread < 0)
                {
                    if (!threadRoots.TryGetValue(s.Thread, out var threadFn))
                        threadRoots[s.Thread] = threadFn = data.InternFunction(data.Threads[s.Thread].Name, "thread", FunctionKind.Synthetic);
                    stack.Insert(0, threadFn);
                }

                tree.TotalNs += s.WeightNs;
                tree.SampleCount++;
                var node = tree.Root;
                node.TotalNs += s.WeightNs;
                for (var i = 0; i < stack.Count; i++)
                {
                    node = node.GetOrAddChild(stack[i], tree.Nodes);
                    node.TotalNs += s.WeightNs;
                    // Top-down: time is "self" at the leaf. Bottom-up: at the first real frame (the leaf, now a root).
                    var selfIndex = options.Inverted ? (options.Thread < 0 ? 1 : 0) : stack.Count - 1;
                    if (i == selfIndex)
                        node.SelfNs += s.WeightNs;
                }
            }

            tree.Root.SortRecursive();
            return tree;
        }

        /// <summary>
        /// Keeps only managed frames plus the first native frame called from managed code
        /// (usually the icall / engine binding where the time actually goes).
        /// </summary>
        static void KeepManaged(ProfileData data, List<int> stack, int nativeOnlyId)
        {
            var lastManaged = -1;
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (data.Functions[stack[i]].Kind == FunctionKind.Managed)
                {
                    lastManaged = i;
                    break;
                }
            }
            if (lastManaged < 0)
            {
                var leaf = stack.Count > 0 ? stack[stack.Count - 1] : nativeOnlyId;
                stack.Clear();
                stack.Add(nativeOnlyId);
                stack.Add(leaf);
                return;
            }

            var write = 0;
            for (var i = 0; i < stack.Count; i++)
            {
                var keep = data.Functions[stack[i]].Kind == FunctionKind.Managed || i == lastManaged + 1;
                if (keep)
                    stack[write++] = stack[i];
            }
            stack.RemoveRange(write, stack.Count - write);
        }
    }
}
