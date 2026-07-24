using UnityEngine.SceneManagement;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Contract for a scene optimizer plugged into <see cref="SceneOptimizerRegistry"/>.
    ///
    /// Optimizers only ever see an already-duplicated scene (never the authored original) and
    /// are responsible for duplicating, in turn, any asset they intend to mutate (e.g. a
    /// TerrainData) into <paramref name="sceneAssetDir"/> before modifying it — see
    /// <see cref="OptimizedSceneGenerator"/> for the duplication flow this plugs into.
    /// </summary>
    public interface ISceneOptimizer
    {
        /// <summary>Stable identifier used to key settings in <see cref="SceneOptimizerSettingsContainer"/>. Do not rename once shipped.</summary>
        string Id { get; }

        /// <summary>Human-readable name shown in the window.</summary>
        string Name { get; }

        /// <summary>
        /// Whether this optimizer has a settings popup to show in the window.
        /// (Whether it actually *runs* is a per-project/per-BuildProfile choice stored in
        /// <see cref="SceneOptimizerSettingsContainer"/>, not a property of the optimizer itself —
        /// see <see cref="SceneOptimizerSettingsContainer.OptimizerSettingsEntry.Enabled"/>.)
        /// </summary>
        bool HasSettings { get; }

        /// <summary>Creates a fresh settings instance with default values, used the first time the container needs one.</summary>
        object CreateDefaultSettings();

        /// <summary>Draws the settings popup content for the given (boxed) settings instance.</summary>
        void DrawSettingsGUI(object settings);

        /// <summary>
        /// Runs this optimizer against a duplicated scene that is safe to mutate in place.
        /// </summary>
        /// <param name="duplicatedScene">The already-open, already-duplicated scene.</param>
        /// <param name="sourceScenePath">Asset path of the authoring scene this was duplicated from (e.g. to exclude it from referrer checks).</param>
        /// <param name="sceneAssetDir">Folder the duplicated scene asset lives in — duplicate any mutated assets here.</param>
        /// <param name="settings">This optimizer's resolved settings (see <see cref="CreateDefaultSettings"/>).</param>
        /// <param name="report">Report to log changes/warnings into.</param>
        void Execute(Scene duplicatedScene, string sourceScenePath, string sceneAssetDir, object settings, SceneOptimizationReport report);
    }
}
