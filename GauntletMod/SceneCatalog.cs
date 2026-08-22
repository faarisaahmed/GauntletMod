using System;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace GauntletMod
{
    /// <summary>
    /// Answers "is that actually a scene?" before we try to load it.
    ///
    /// This exists because of a specific, nasty failure: asking Silksong to load a
    /// scene name that isn't in the Addressables catalog doesn't error. The load
    /// operation simply never completes, so the game sits on the loading screen
    /// forever with nothing in the log. One typo in a config value costs you a force
    /// quit and tells you nothing about why.
    ///
    /// A resource-location lookup is a cheap in-memory catalog query, so it's safe to
    /// do synchronously right before committing to a load.
    /// </summary>
    internal static class SceneCatalog
    {
        /// <summary>Used when the configured room turns out not to exist.</summary>
        public const string DefaultGauntletScene = "Hang_04";

        private static readonly Dictionary<string, bool> Cache = new Dictionary<string, bool>();

        public static bool Exists(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            if (Cache.TryGetValue(sceneName, out bool cached)) return cached;

            bool found;
            try
            {
                AsyncOperationHandle<IList<IResourceLocation>> handle =
                    Addressables.LoadResourceLocationsAsync("Scenes/" + sceneName);

                IList<IResourceLocation> locations = handle.WaitForCompletion();
                found = locations != null && locations.Count > 0;
                Addressables.Release(handle);
            }
            catch (Exception e)
            {
                // If the lookup itself breaks, don't block a load that might be fine -
                // but say so, because we've just lost the safety net.
                Plugin.Log.LogWarning($"SceneCatalog: couldn't check '{sceneName}' ({e.Message}); assuming it exists.");
                return true;
            }

            Cache[sceneName] = found;
            return found;
        }

        /// <summary>
        /// Returns a scene name that definitely loads: the configured one if it's
        /// real, otherwise the default, otherwise null. Repairs the config as a side
        /// effect so the bad value doesn't bite again next launch.
        /// </summary>
        public static string ResolveGauntletScene()
        {
            string configured = GauntletConfig.GauntletScene.Value;

            if (Exists(configured)) return configured;

            Plugin.Log.LogError(
                $"Custom Gauntlet: '{configured}' isn't a scene in this build of Silksong. Loading it " +
                "would hang the game on the loading screen forever, so it's been reset to " +
                $"'{DefaultGauntletScene}' (the Grand Forum). Use F9 -> 'Use this room as the gauntlet' " +
                "if you want a different room.");

            if (!Exists(DefaultGauntletScene))
            {
                Plugin.Log.LogError($"Custom Gauntlet: '{DefaultGauntletScene}' isn't loadable either. Not starting.");
                return null;
            }

            GauntletConfig.GauntletScene.Value = DefaultGauntletScene;
            return DefaultGauntletScene;
        }
    }
}
