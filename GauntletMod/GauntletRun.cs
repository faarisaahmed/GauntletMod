using System;
using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// Tracks a single run: when it started, when it finished, and what to say about
    /// it afterwards. Also owns the "you can fiddle with your tools between fights"
    /// rule.
    /// </summary>
    internal static class GauntletRun
    {
        public static bool ShowingResult { get; private set; }
        public static string ResultTitle { get; private set; } = "";
        public static string ResultBody { get; private set; } = "";

        private static float _startedAt;
        private static int _waveCount;

        /// <summary>Called when our waves are set going.</summary>
        public static void Begin(int waves)
        {
            ShowingResult = false;
            _startedAt = Time.unscaledTime;
            _waveCount = waves;
        }

        /// <summary>Called when the last enemy of the last wave goes down.</summary>
        public static void Complete()
        {
            if (ShowingResult) return;

            float seconds = Mathf.Max(0f, Time.unscaledTime - _startedAt);
            var span = TimeSpan.FromSeconds(seconds);

            ResultTitle = "GAUNTLET CLEARED";
            ResultBody =
                $"{_waveCount} wave{(_waveCount == 1 ? "" : "s")} in {span.Minutes:0}:{span.Seconds:00}." +
                "\n\nRest at the bench to change your tools, or run it again.";

            if (GauntletConfig.ShowClearMessage.Value)
            {
                ShowingResult = true;
                GauntletPanel.ShowCursorForOverlay();
            }

            Plugin.Log.LogInfo($"Custom Gauntlet: run cleared - {_waveCount} wave(s) in {seconds:0.0}s.");
        }

        public static void Dismiss()
        {
            ShowingResult = false;
            GauntletPanel.HideCursor();
        }

        public static void Restart()
        {
            Dismiss();
            ResetArena("another run");

            // Put you back at the bench rather than starting the fight on the spot -
            // walking in is how a run is meant to begin, and it gives you a moment to
            // change tools first.
            ArenaSetup.MoveHeroTo(ArenaSetup.HomePoint(), "back to the bench for another run");
        }

        /// <summary>
        /// Puts the arena back to a clean wave one.
        ///
        /// Dying used to leave the fight mid-run: the BattleScene kept its wave
        /// counter, so walking back in resumed from wherever you'd got to - and if the
        /// room had re-armed its own battle in the meantime you could get the stock
        /// High Halls fight instead of yours, or one after the other. Re-adopting is
        /// what makes it always ours: it disarms every other battle in the room and
        /// clears anything the room brought back before rebuilding your waves from the
        /// top.
        /// </summary>
        public static void ResetArena(string why)
        {
            try
            {
                ArenaSetup.ReleaseArenaLocks();
                WaveBuilder.AdoptRoomBattle();
                ArenaSetup.StripRoom(includeInactive: true);
                ArenaSetup.SealExits();

                if (WaveConfig.IsEmpty)
                {
                    WaveBuilder.Disarm();
                    return;
                }

                if (WaveBuilder.Rebuild())
                {
                    Plugin.Log.LogInfo($"Custom Gauntlet: arena reset for {why} - back to wave one.");
                }
                else
                {
                    Plugin.Log.LogWarning($"Custom Gauntlet: couldn't rebuild the waves for {why}.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: arena reset failed: " + e);
            }
        }

        /// <summary>
        /// Lets you change tools and crests anywhere, but only while no fight is
        /// running.
        ///
        /// This is the game's own developer switch - InventoryItemToolManager falls
        /// back to CheatManager.CanChangeEquipsAnywhere whenever you aren't sitting on
        /// a bench - so it needs no patching and behaves exactly as the real thing
        /// does at a bench. Turning it off again once the waves start keeps the fight
        /// honest.
        /// </summary>
        public static void UpdateEquipFreedom()
        {
            if (!GauntletConfig.ToolsBetweenFights.Value) return;
            if (!GauntletMode.Active || !GauntletMode.InGauntletRoom) return;

            bool allowed = !WaveBuilder.IsBattleRunning;
            if (CheatManager.CanChangeEquipsAnywhere != allowed)
            {
                CheatManager.CanChangeEquipsAnywhere = allowed;
            }
        }

        public static void Reset()
        {
            ShowingResult = false;
            _waveCount = 0;
        }
    }
}
