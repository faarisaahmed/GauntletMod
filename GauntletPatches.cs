using System;
using System.Collections.Generic;
using GlobalEnums;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// Four small hooks. Every one of them no-ops unless the player actually chose
    /// Custom Gauntlet, so a normal playthrough behaves exactly as shipped.
    /// </summary>
    [HarmonyPatch]
    internal static class GauntletPatches
    {
        // ------------------------------------------------------------------
        // 1. "New Game" -> game mode menu
        // ------------------------------------------------------------------
        // Stock behaviour: picking an empty save slot only opens the play-mode
        // menu if Steel Soul or Boss Rush have been unlocked; otherwise it starts
        // a normal game immediately. We always route through the play-mode menu so
        // there's somewhere to put the Custom Gauntlet button. The buttons on that
        // menu call StartNewGame again, and at that point menuState is
        // PLAY_MODE_MENU, so the call passes straight through.
        [HarmonyPatch(typeof(UIManager), nameof(UIManager.StartNewGame))]
        [HarmonyPrefix]
        private static bool UIManager_StartNewGame_Prefix(UIManager __instance)
        {
            try
            {
                if (!GauntletConfig.ShowModeMenuOnNewGame.Value) return true;
                if (GauntletMode.Starting) return true;                        // our own call
                if (__instance.menuState == MainMenuState.PLAY_MODE_MENU) return true; // came from the menu

                // On a first-ever launch the overscan/brightness screens interrupt
                // the start and then call back in via UIStartNewGameContinue. Sending
                // that back to the mode menu would just make the player choose twice.
                if (__instance.menuState == MainMenuState.OVERSCAN_MENU ||
                    __instance.menuState == MainMenuState.BRIGHTNESS_MENU) return true;

                MenuInjector.RequestInject();
                __instance.UIGoToPlayModeMenu();
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("UIManager.StartNewGame prefix failed, falling through: " + e);
                return true;
            }
        }

        // ------------------------------------------------------------------
        // 2. Point the brand-new save at the arena + max the loadout
        // ------------------------------------------------------------------
        // GameManager.StartNewGame creates the fresh PlayerData singleton, so this
        // is the first moment the gauntlet's respawn scene and loadout can be set.
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartNewGame))]
        [HarmonyPostfix]
        private static void GameManager_StartNewGame_Postfix()
        {
            if (!GauntletMode.Starting) return;

            try
            {
                GauntletMode.OnFreshPlayerData();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: OnFreshPlayerData failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // 3. Force the respawn target to be our arena
        // ------------------------------------------------------------------
        // GetRespawnInfo validates the saved respawn scene/marker against
        // SceneTeleportMap and quietly falls back to Tut_01 + "Death Respawn Marker
        // Init" for anything it can't match - which includes a blank marker name, so
        // without this the very first gauntlet start would drop you in Moss Grotto.
        // Real bench respawns are left alone; see GauntletMode.ResolveRespawn.
        [HarmonyPatch(typeof(GameManager), "GetRespawnInfo")]
        [HarmonyPostfix]
        private static void GameManager_GetRespawnInfo_Postfix(ref string scene, ref string marker)
        {
            GauntletMode.ResolveRespawn(ref scene, ref marker);
        }

        // ------------------------------------------------------------------
        // 4. Keep an imported room's colours to itself
        // ------------------------------------------------------------------
        // Every room carries a SceneColorManager that pushes its palette into the
        // global colour-correction curves. Load a room in the background and it
        // recolours the room you're actually standing in, and unloading it doesn't put
        // that back - the tint had already been applied.
        //
        // Repairing it afterwards half-worked; refusing to let it happen is cleaner.
        // While an import is running, any colour manager that isn't the arena's own is
        // skipped outright.
        [HarmonyPatch(typeof(SceneColorManager), nameof(SceneColorManager.UpdateScript))]
        [HarmonyPrefix]
        private static bool SceneColorManager_UpdateScript_Prefix(SceneColorManager __instance)
        {
            if (!EnemyImporter.Busy) return true;

            GameCameras cameras = GameCameras.instance;
            if (cameras != null && cameras.sceneColorManager == __instance) return true;

            return false; // an imported room's palette - not this room's business
        }

        // ------------------------------------------------------------------
        // 5. Notice when a run is cleared
        // ------------------------------------------------------------------
        // DoEndBattle is what BattleScene calls once the final enemy of the final
        // wave goes down, before its own end-of-fight fanfare and gate opening. It's
        // the cleanest "you won" signal available, and postfixing it means our
        // message rides along with the game's own celebration rather than fighting it.
        [HarmonyPatch(typeof(BattleScene), nameof(BattleScene.DoEndBattle))]
        [HarmonyPostfix]
        private static void BattleScene_DoEndBattle_Postfix(BattleScene __instance)
        {
            if (!GauntletMode.Active) return;
            if (__instance != WaveBuilder.CurrentBattle) return;

            try
            {
                ArenaSetup.UnsealExits();
                ArenaSetup.ReleaseArenaLocks();
                GauntletRun.Complete();
            }
            catch (Exception e) { Plugin.Log.LogError("Custom Gauntlet: run completion failed: " + e); }
        }

        // ------------------------------------------------------------------
        // 6. Land somewhere real, whatever kind of death it was
        // ------------------------------------------------------------------
        // FindEntryPoint is the single place GameManager decides where to put Hornet
        // when a scene begins, and it has three different answers depending on how you
        // got there: a respawn marker after a normal death, playerData.
        // hazardRespawnLocation after a pit or spike, or a transition point otherwise.
        //
        // That middle one is the trap. hazardRespawnLocation is (0,0,0) on a fresh
        // save - the bottom-left corner of the room - and nothing in the gauntlet ever
        // sets it, because the hazard triggers that normally would have been stripped
        // out with the rest of the room. Patching the marker lookup never touched it.
        //
        // Overriding here covers every route at once.
        [HarmonyPatch(typeof(GameManager), "FindEntryPoint")]
        [HarmonyPostfix]
        private static void GameManager_FindEntryPoint_Postfix(ref Vector2? __result)
        {
            if (!GauntletMode.Active) return;

            // Only step in when the game has no answer at all. An earlier version also
            // rejected any answer outside the room's "safe" margin, which sounded
            // careful and was actively wrong: doors sit at room edges by definition, so
            // every legitimate doorway arrival got rejected and teleported to the
            // gauntlet's home point instead - including when leaving for another room.
            if (__result.HasValue) return;

            // And only for our own room; other rooms are none of our business.
            if (!GauntletMode.IsGauntletRoom(SceneManager.GetActiveScene().name)) return;

            try
            {
                if (!ArenaSetup.TryHomePoint(out Vector3 home)) return;

                Plugin.Log.LogInfo(
                    $"Custom Gauntlet: the game had no entry point - using the home point " +
                    $"({home.x:0.#}, {home.y:0.#}).");

                __result = new Vector2(home.x, home.y);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: entry point override failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // 6. Never spawn Hornet into the void
        // ------------------------------------------------------------------
        // On the respawn path, GameManager.FindEntryPoint uses
        // HeroController.LocateSpawnPoint() and, if that returns null, positions
        // the hero at (-20000, 20000) - i.e. an endless fall. Our marker name is
        // usually blank (marker names differ per room), so we supply a sane one:
        // the bench, else any respawn marker, else any door.
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.LocateSpawnPoint))]
        [HarmonyPostfix]
        private static void HeroController_LocateSpawnPoint_Postfix(ref Transform __result)
        {
            if (!GauntletMode.Active || __result != null) return;

            try
            {
                Transform fallback = ArenaSetup.FindSafeSpawn();
                if (fallback != null)
                {
                    Plugin.Log.LogInfo($"Custom Gauntlet: no matching respawn marker, using '{fallback.name}'.");
                    __result = fallback;
                }
                else
                {
                    Plugin.Log.LogWarning("Custom Gauntlet: no spawn point found in arena at all.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: LocateSpawnPoint fallback failed: " + e);
            }
        }
    }
}
