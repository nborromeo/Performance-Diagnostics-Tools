using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Global registry of <see cref="ISceneOptimizer"/> instances.
    /// Optimizers self-register from their <c>[InitializeOnLoad]</c> static constructors.
    /// </summary>
    public static class SceneOptimizerRegistry
    {
        static readonly List<ISceneOptimizer> s_Optimizers = new List<ISceneOptimizer>();
        static readonly Dictionary<ISceneOptimizer, int> s_RegistrationSequence = new Dictionary<ISceneOptimizer, int>();
        static int s_NextSequence;

        /// <summary>
        /// Registered optimizers sorted by <see cref="ISceneOptimizer.Order"/> (ties broken by
        /// registration order), so callers (the window UI, <see cref="OptimizedSceneGenerator"/>)
        /// always see a consistent, dependency-correct order.
        /// </summary>
        public static IReadOnlyList<ISceneOptimizer> Optimizers => s_Optimizers;

        /// <summary>Fires whenever an optimizer is registered or unregistered.</summary>
        public static event Action OptimizerListChanged;

        public static void Register(ISceneOptimizer optimizer)
        {
            if (s_Optimizers.Contains(optimizer))
                return;

            s_Optimizers.Add(optimizer);
            s_RegistrationSequence[optimizer] = s_NextSequence++;
            Resort();
            OptimizerListChanged?.Invoke();
        }

        public static void Unregister(ISceneOptimizer optimizer)
        {
            if (!s_Optimizers.Remove(optimizer))
                return;

            s_RegistrationSequence.Remove(optimizer);
            OptimizerListChanged?.Invoke();
        }

        static void Resort()
        {
            var sorted = s_Optimizers
                .OrderBy(o => o.Order)
                .ThenBy(o => s_RegistrationSequence[o])
                .ToList();
            s_Optimizers.Clear();
            s_Optimizers.AddRange(sorted);
        }
    }
}
