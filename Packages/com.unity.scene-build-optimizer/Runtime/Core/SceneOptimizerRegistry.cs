using System;
using System.Collections.Generic;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Global registry of <see cref="ISceneOptimizer"/> instances.
    /// Optimizers self-register from their <c>[InitializeOnLoad]</c> static constructors.
    /// </summary>
    public static class SceneOptimizerRegistry
    {
        static readonly List<ISceneOptimizer> s_Optimizers = new List<ISceneOptimizer>();

        public static IReadOnlyList<ISceneOptimizer> Optimizers => s_Optimizers;

        /// <summary>Fires whenever an optimizer is registered or unregistered.</summary>
        public static event Action OptimizerListChanged;

        public static void Register(ISceneOptimizer optimizer)
        {
            if (!s_Optimizers.Contains(optimizer))
            {
                s_Optimizers.Add(optimizer);
                OptimizerListChanged?.Invoke();
            }
        }

        public static void Unregister(ISceneOptimizer optimizer)
        {
            if (s_Optimizers.Remove(optimizer))
                OptimizerListChanged?.Invoke();
        }
    }
}
