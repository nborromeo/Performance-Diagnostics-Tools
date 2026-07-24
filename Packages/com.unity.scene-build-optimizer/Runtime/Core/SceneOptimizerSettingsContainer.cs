using System;
using System.Collections.Generic;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace SceneBuildOptimizer
{
    /// <summary>
    /// Project-wide settings for every registered <see cref="ISceneOptimizer"/>, plus optional
    /// per-<see cref="BuildProfile"/> overrides — of both which optimizers run and their settings.
    ///
    /// BuildProfile has no public extension point for third-party override sections (its surface
    /// is limited to scenes/scriptingDefines — see the plan doc), so this is the closest practical
    /// equivalent: one settings asset, resolved against a specific profile via <see cref="ResolveEntry"/>.
    /// That profile is always passed in explicitly by the caller (e.g. the pre-build check fetches
    /// <see cref="BuildProfile.GetActiveBuildProfile"/> itself) — this container never reaches for the
    /// "active" profile on its own, since that's easy-to-miss ambient Unity state.
    /// </summary>
    public sealed class SceneOptimizerSettingsContainer : ScriptableObject
    {
        [Serializable]
        public sealed class OptimizerSettingsEntry
        {
            public string OptimizerId;

            /// <summary>Whether this optimizer runs. On a default entry this is the project-wide default; on a profile override entry it's specific to that profile.</summary>
            public bool Enabled = true;

            [SerializeReference] public object Settings;
        }

        [Serializable]
        public sealed class BuildProfileSettingsOverride
        {
            public BuildProfile Profile;

            /// <summary>Which optimizers this profile has its own configuration for — an optimizer with no entry here just inherits the project-wide default.</summary>
            public List<OptimizerSettingsEntry> Overrides = new List<OptimizerSettingsEntry>();
        }

        [SerializeField] List<OptimizerSettingsEntry> m_DefaultSettings = new List<OptimizerSettingsEntry>();
        [SerializeField] List<BuildProfileSettingsOverride> m_ProfileOverrides = new List<BuildProfileSettingsOverride>();
        [SerializeField] List<UnityEditor.SceneAsset> m_TrackedScenes = new List<UnityEditor.SceneAsset>();

        public List<OptimizerSettingsEntry> DefaultSettings => m_DefaultSettings;
        public List<BuildProfileSettingsOverride> ProfileOverrides => m_ProfileOverrides;

        /// <summary>Scenes the window shows in its list — purely a UI convenience, not used by the pre-build check (which discovers optimized scenes via Build Profiles/Build Settings instead).</summary>
        public List<UnityEditor.SceneAsset> TrackedScenes => m_TrackedScenes;

        /// <summary>
        /// Resolves the entry (enabled state + settings) for an optimizer against a specific profile's
        /// override if one exists, else the project-wide default.
        /// </summary>
        /// <param name="profile">
        /// The profile to resolve an override against, or null to use the project-wide default only.
        /// Deliberately explicit, not implicitly <see cref="BuildProfile.GetActiveBuildProfile"/> —
        /// "the active build profile" is easy-to-miss ambient Unity state (whatever's currently
        /// selected in the Build Profiles window), unrelated to whatever the caller actually intends.
        /// Callers that do want that — e.g. the pre-build check, which should match whatever profile
        /// Unity is really building with — must fetch and pass it explicitly.
        /// </param>
        public OptimizerSettingsEntry ResolveEntry(string optimizerId, Func<object> createDefault, BuildProfile profile)
        {
            if (profile != null)
            {
                var profileOverride = m_ProfileOverrides.Find(o => o.Profile == profile);
                var overrideEntry = profileOverride?.Overrides.Find(e => e.OptimizerId == optimizerId);
                if (overrideEntry != null)
                {
                    if (overrideEntry.Settings == null)
                        overrideEntry.Settings = createDefault();
                    return overrideEntry;
                }
            }

            return GetOrCreateDefaultEntry(optimizerId, createDefault);
        }

        /// <summary>Convenience for callers that only need the resolved settings object (e.g. a settings popup) against a specific profile, not the enabled flag.</summary>
        public object GetEffectiveSettings(string optimizerId, Func<object> createDefault, BuildProfile profile) =>
            ResolveEntry(optimizerId, createDefault, profile).Settings;

        public OptimizerSettingsEntry GetOrCreateDefaultEntry(string optimizerId, Func<object> createDefault)
        {
            var entry = m_DefaultSettings.Find(e => e.OptimizerId == optimizerId);
            if (entry == null)
            {
                entry = new OptimizerSettingsEntry { OptimizerId = optimizerId, Settings = createDefault() };
                m_DefaultSettings.Add(entry);
            }
            else if (entry.Settings == null)
            {
                entry.Settings = createDefault();
            }

            return entry;
        }

        public OptimizerSettingsEntry GetOrCreateProfileOverride(BuildProfile profile, string optimizerId, Func<object> createDefault)
        {
            var profileOverride = m_ProfileOverrides.Find(o => o.Profile == profile);
            if (profileOverride == null)
            {
                profileOverride = new BuildProfileSettingsOverride { Profile = profile };
                m_ProfileOverrides.Add(profileOverride);
            }

            var entry = profileOverride.Overrides.Find(e => e.OptimizerId == optimizerId);
            if (entry == null)
            {
                // Enabled defaults to true (not the project default's Enabled) regardless of the
                // project-wide default: adding a profile override is, in practice, almost always
                // someone deliberately turning an optimizer on/configuring it for that one profile —
                // seeding it disabled-by-inheritance here would silently defeat that intent.
                entry = new OptimizerSettingsEntry { OptimizerId = optimizerId, Enabled = true, Settings = createDefault() };
                profileOverride.Overrides.Add(entry);
            }

            return entry;
        }

        public void RemoveProfileOverride(BuildProfile profile, string optimizerId)
        {
            var profileOverride = m_ProfileOverrides.Find(o => o.Profile == profile);
            profileOverride?.Overrides.RemoveAll(e => e.OptimizerId == optimizerId);
        }
    }
}
