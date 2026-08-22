using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GauntletMod
{
    // Unique GUID - don't reuse this for other mods.
    [BepInPlugin(Guid, "Custom Gauntlet", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.custom.gauntletmod";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            GauntletConfig.Bind(Config);

            try
            {
                _harmony = new Harmony(Guid);
                _harmony.PatchAll(typeof(GauntletPatches));
                Log.LogInfo("Custom Gauntlet: Harmony patches applied.");
            }
            catch (Exception e)
            {
                // Without the patches the menu entry can't start a run, but the dev
                // menu still works - better than taking the game down.
                Log.LogError("Custom Gauntlet: Harmony patching failed: " + e);
            }

            // GauntletManager is a separate MonoBehaviour so it can run coroutines
            // and OnGUI independently of the plugin object.
            var go = new GameObject("GauntletManager");
            go.transform.SetParent(transform);
            go.AddComponent<GauntletManager>();

            Log.LogInfo($"Custom Gauntlet loaded. Press {GauntletConfig.DevMenuKey.Value} for the dev menu.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
