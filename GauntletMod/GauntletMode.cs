using System;
using System.Collections;
using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// The mode itself: what happens between clicking "Custom Gauntlet" in the menu
    /// and standing on the bench outside the arena.
    ///
    /// The start sequence does NOT reimplement new-game. Silksong's
    /// GameManager.StartNewGame(permadeath, bossRush) already has two branches:
    ///
    ///   bossRush == false -> RunStartNewGame()  -> loads "Opening_Sequence" (the intro)
    ///   bossRush == true  -> RunContinueGame()  -> loads playerData.respawnScene directly
    ///
    /// Taking the second branch gets us a correct, cutscene-free start for free. The
    /// one thing it does that needs overriding is GameManager.GetRespawnInfo(), which
    /// validates the respawn scene against SceneTeleportMap; GauntletPatches routes
    /// that through <see cref="ResolveRespawn"/>.
    ///
    /// It's all one room. The arena is the left-hand end of the High Halls gauntlet
    /// scene and the bench sits at the right-hand end of that same scene, so there's
    /// one scene to load and a home *position* within it.
    /// </summary>
    internal static class GauntletMode
    {
        /// <summary>True from the menu button press until we've landed. Gates the patches.</summary>
        public static bool Starting;

        /// <summary>True while the player is inside the gauntlet at all.</summary>
        public static bool Active;

        /// <summary>Marker the room actually used, so deaths land in the same spot.</summary>
        public static string ResolvedMarkerName = "";

        public static bool InGauntletRoom { get; private set; }

        /// <summary>
        /// True only once the room has been fully set up: enemies harvested, battles
        /// adopted, waves built. The enemy sweep waits on this - starting earlier
        /// would delete the room's stock enemies before they've been banked into the
        /// picker, leaving you with an arena and nothing to put in it.
        /// </summary>
        public static bool RoomReady { get; private set; }

        public static string GauntletScene => GauntletConfig.GauntletScene.Value;

        /// <summary>
        /// The gauntlet is one room, not two. The arena is its left-hand end and the
        /// bench sits at its right-hand end, so there's a single scene to load and a
        /// home *position* within it - see ArenaSetup.HomePoint.
        /// </summary>
        public static string FirstScene => GauntletScene;

        public static bool IsGauntletRoom(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) && sceneName == GauntletScene;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Wired to the injected menu button. Signature must stay "void ()" - it's
        /// registered as a UnityAction on MenuButton.OnSubmitPressed.
        /// </summary>
        public static void StartFromMenu()
        {
            try
            {
                var ui = UIManager.instance;
                var gm = GameManager.instance;
                if (ui == null || gm == null)
                {
                    Plugin.Log.LogError("Custom Gauntlet: UIManager/GameManager missing, cannot start.");
                    return;
                }

                // Check the room is real before committing. A name that isn't in the
                // Addressables catalog doesn't fail the load - it just never finishes,
                // and the game sits on the loading screen indefinitely.
                if (SceneCatalog.ResolveGauntletScene() == null) return;

                Starting = true;
                Active = true;
                ResolvedMarkerName = GauntletConfig.ArenaRespawnMarker.Value ?? "";

                // Mark the slot now, so continuing this save later re-enters the mode.
                RememberGauntletProfile(gm.profileID);

                WaveConfig.Load();

                Plugin.Log.LogInfo($"Custom Gauntlet: starting into '{FirstScene}' (profile {gm.profileID}).");

                // bossRush: true is what skips Opening_Sequence. permaDeath stays false.
                ui.StartNewGame(false, true);
            }
            catch (Exception e)
            {
                Starting = false;
                Active = false;
                Plugin.Log.LogError("Custom Gauntlet: failed to start: " + e);
            }
        }

        /// <summary>Called right after GameManager.StartNewGame builds the fresh PlayerData.</summary>
        public static void OnFreshPlayerData()
        {
            var pd = PlayerData.instance;
            if (pd == null)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: PlayerData was null right after StartNewGame.");
                return;
            }

            pd.respawnScene = FirstScene;
            pd.respawnMarkerName = ResolvedMarkerName;
            pd.respawnType = 0;

            // Act 3 is a world state, not a room property: blackThreadWorld is what
            // SaveStats reads as IsAct3, and rooms dress themselves from it. A fresh
            // PlayerData should already have it clear, so this is a belt-and-braces
            // reset - but it logs what it found, because if it was already false then
            // the Citadel's odd shading is coming from somewhere else and worth
            // chasing separately.
            if (pd.blackThreadWorld)
            {
                Plugin.Log.LogInfo("Custom Gauntlet: save was flagged as Act 3 (blackThreadWorld); clearing it.");
                pd.blackThreadWorld = false;
            }
            else
            {
                Plugin.Log.LogInfo("Custom Gauntlet: save is not flagged as Act 3 - any Act 3 look is the room's own dressing.");
            }

            if (GauntletConfig.MaxLoadoutOnStart.Value)
            {
                // Stats only at this point - ToolItemManager isn't up until we're in a
                // gameplay scene, so tools are granted again on arrival.
                PlayerBuffs.ApplyFullLoadout();
            }
        }

        /// <summary>
        /// Decides where a respawn actually goes. Bench rests are left alone; only
        /// respawns that would otherwise fall back to Tut_01 get redirected.
        /// </summary>
        public static void ResolveRespawn(ref string scene, ref string marker)
        {
            if (!Active) return;

            if (scene != FirstScene)
            {
                Plugin.Log.LogInfo($"Custom Gauntlet: redirecting respawn '{scene}' -> '{FirstScene}'.");
            }

            scene = FirstScene;
            marker = ResolvedMarkerName ?? "";
        }

        /// <summary>
        /// Re-arms the mode when an existing gauntlet run is continued.
        ///
        /// Identified by save slot, not by respawn scene. The staging room is a real
        /// Pharloom room, so "your respawn is in that room" would also be true of a
        /// normal playthrough parked there - and re-arming on that would strip a real
        /// save's room of its enemies.
        /// </summary>
        public static void ReArmIfGauntletSave()
        {
            if (Active) return;

            GameManager gm = GameManager.instance;
            if (gm == null || !IsGauntletProfile(gm.profileID)) return;

            Active = true;
            WaveConfig.Load();
            Plugin.Log.LogInfo($"Custom Gauntlet: save slot {gm.profileID} is a gauntlet run, re-arming mode.");
        }

        public static bool IsGauntletProfile(int profileId)
        {
            string raw = GauntletConfig.GauntletProfileIds.Value;
            if (string.IsNullOrEmpty(raw)) return false;

            foreach (string part in raw.Split(','))
            {
                if (int.TryParse(part.Trim(), out int id) && id == profileId) return true;
            }
            return false;
        }

        private static void RememberGauntletProfile(int profileId)
        {
            if (IsGauntletProfile(profileId)) return;

            string raw = GauntletConfig.GauntletProfileIds.Value;
            GauntletConfig.GauntletProfileIds.Value =
                string.IsNullOrEmpty(raw) ? profileId.ToString() : raw + "," + profileId;
        }

        // ------------------------------------------------------------------

        /// <summary>Guards against two setup passes running over each other.</summary>
        public static bool SettingUp { get; private set; }

        /// <summary>Runs once the gauntlet room is live, and sets the whole thing up.</summary>
        public static IEnumerator OnRoomLoaded()
        {
            if (SettingUp) yield break;
            SettingUp = true;
            // Let the room's own Awake/Start/FSM-init pass finish before we start
            // deleting things out from under it.
            yield return null;
            yield return null;
            yield return new WaitForSeconds(0.25f);

            InGauntletRoom = true;
            RoomReady = false;
            bool arriving = Starting;
            Starting = false;

            if (GauntletConfig.MaxLoadoutOnStart.Value)
            {
                // Second pass: this is where tools actually land, because
                // ToolItemManager only exists inside a gameplay scene.
                PlayerBuffs.ApplyFullLoadout();
            }

            // The Grand Forum's arena arrives in a second scene (Hang_04_boss) loaded
            // additively on top of the room, and it isn't necessarily there the
            // instant the room is. Give it a moment before deciding there's no battle.
            // Wait for an *active* battle. Hang_04 has an inactive "Battle Scene Act3"
            // sitting in it and only gets the live one when Hang_04_boss finishes
            // loading additively - stopping at the first BattleScene we see would pick
            // the dead one.
            yield return WaitForBattleScene(4f);

            Plugin.Log.LogInfo($"Custom Gauntlet: room scenes loaded: {ArenaSetup.DescribeLoadedScenes()}");

            // Bank the room's own roster before anything clears it.
            if (GauntletConfig.HarvestEnemies.Value) EnemyLibrary.HarvestActiveScene();

            // Adopt the room's BattleScene and empty its stock waves. This is what
            // stops the original Forum fight running alongside yours - the "double
            // gauntlet" - and it has to happen before StripRoom so the stock enemies
            // get harvested rather than just deleted.
            BattleScene battle = WaveBuilder.AdoptRoomBattle();

            // includeInactive: the gauntlet's enemies sit dormant until their wave
            // turns them on, so anything less leaves the whole stock fight in place.
            ArenaSetup.StripRoom(includeInactive: true);
            ArenaSetup.SealExits();

            if (battle != null)
            {
                if (WaveConfig.IsEmpty) WaveBuilder.Disarm();
                else WaveBuilder.Rebuild();
            }

            // Put the bench at the quiet end of the room, then stand on it. Order
            // matters: the bench needs the home point, and the respawn marker we
            // record afterwards should be the bench's own if it brought one.
            // Same reliability rule as the spawn: a bench dropped at a point we've
            // guessed wrong is a bench embedded in a wall.
            if (GauntletConfig.AddBench.Value && ArenaSetup.TryHomePoint(out _))
            {
                yield return BenchImporter.EnsureBench(ArenaSetup.BenchPlacementPoint());
            }
            else if (GauntletConfig.AddBench.Value)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: skipping the bench until the home point is pinned (F9).");
            }

            // Always reposition, not just on arrival. Deaths and bench rests go
            // through the game's respawn-marker lookup, and in a room we've rearranged
            // that lands wherever the room's own marker happens to be - which in
            // Hang_04 is the far left, nowhere near the bench.
            yield return null;

            if (ArenaSetup.TryHomePoint(out Vector3 home))
            {
                ArenaSetup.MoveHeroTo(home, arriving ? "gauntlet home point" : "back to the home point");
            }
            else
            {
                Plugin.Log.LogWarning(
                    "Custom Gauntlet: not confident where the home point is, so you've been left " +
                    "where the game put you. Walk to where you want to start and use " +
                    "F9 -> 'Set home point here'.");
            }

            ArenaSetup.RecordSpawnMarker();
            ArenaSetup.ReportContents();

            // Everything's banked and built - the sweep can safely start now.
            RoomReady = true;
            SettingUp = false;

            // The HUD's slide-in FSM isn't necessarily up yet, so nudge it a few
            // times rather than once.
            for (int i = 0; i < 4; i++)
            {
                ArenaSetup.ShowHud(verbose: i == 3);
                yield return new WaitForSeconds(0.5f);
            }
        }

        private static IEnumerator WaitForBattleScene(float seconds)
        {
            float waited = 0f;
            while (waited < seconds)
            {
                foreach (BattleScene candidate in UnityEngine.Object.FindObjectsByType<BattleScene>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (candidate == null) continue;
                    if (!ArenaSetup.IsRoomObject(candidate.gameObject)) continue;

                    // Only an active one is worth stopping for - an inactive battle
                    // can't run coroutines, so it can't run a fight either.
                    if (candidate.gameObject.activeInHierarchy) yield break;
                }

                waited += 0.25f;
                yield return new WaitForSeconds(0.25f);
            }
        }

        /// <summary>Marks this room as ours after a manual takeover from the dev menu.</summary>
        public static void ForceInRoom()
        {
            Active = true;
            InGauntletRoom = true;
            RoomReady = true;
        }

        public static void Reset()
        {
            Starting = false;
            Active = false;
            InGauntletRoom = false;
            RoomReady = false;
            SettingUp = false;
        }
    }
}
