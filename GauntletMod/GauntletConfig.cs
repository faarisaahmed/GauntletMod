using BepInEx.Configuration;
using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// All tunables live here so nothing else in the mod has to hardcode a scene
    /// name or a key. Everything is written to
    /// BepInEx/config/com.custom.gauntletmod.cfg on first launch and can be edited
    /// there (or live, via ConfigurationManager) without a rebuild.
    /// </summary>
    internal static class GauntletConfig
    {
        // --- Menu ---
        public static ConfigEntry<string> ModeName;
        public static ConfigEntry<string> ModeDescription;

        // --- Room ---
        public static ConfigEntry<string> GauntletScene;
        public static ConfigEntry<bool> HomePointSet;
        public static ConfigEntry<float> HomePointX;
        public static ConfigEntry<float> HomePointY;
        public static ConfigEntry<string> BenchDonorScene;
        public static ConfigEntry<float> BenchYOffset;
        public static ConfigEntry<bool> AddBench;

        // --- Behaviour ---
        public static ConfigEntry<bool> StripArena;
        public static ConfigEntry<bool> SealExits;
        public static ConfigEntry<bool> HarvestEnemies;
        public static ConfigEntry<bool> KeepRoomClear;
        public static ConfigEntry<bool> RescueOutOfBounds;
        public static ConfigEntry<bool> SpawnAtArenaCentre;
        public static ConfigEntry<bool> ToolsBetweenFights;
        public static ConfigEntry<float> FirstWaveDelay;
        public static ConfigEntry<bool> ShowClearMessage;
        public static ConfigEntry<float> SpawnHeight;
        public static ConfigEntry<string> ArenaRespawnMarker;

        // --- Mode ---
        public static ConfigEntry<bool> ShowModeMenuOnNewGame;
        public static ConfigEntry<bool> MaxLoadoutOnStart;

        // --- Startup ---
        public static ConfigEntry<bool> SkipStartupLogos;

        // --- Bookkeeping ---
        public static ConfigEntry<string> GauntletProfileIds;

        // --- Keys ---
        public static ConfigEntry<KeyCode> PanelKey;
        public static ConfigEntry<KeyCode> DevMenuKey;
        public static ConfigEntry<KeyCode> FreeMoveKey;
        public static ConfigEntry<KeyCode> ReadoutKey;
        public static ConfigEntry<KeyCode> ZoomOutKey;
        public static ConfigEntry<KeyCode> ZoomInKey;
        public static ConfigEntry<float> FreeMoveSpeed;

        public static void Bind(ConfigFile cfg)
        {
            // Kept short and in the game's register - the mode buttons sit beside
            // "Normal" and "Steel Soul", and anything chattier reads as a mod bolted
            // on. Both are config so you can reword them without a rebuild; the change
            // shows up next time you reach the title screen.
            ModeName = cfg.Bind(
                "Menu", "ModeName", "CUSTOM GAUNTLET",
                "Name shown on the game-mode button.");

            ModeDescription = cfg.Bind(
                "Menu", "ModeDescription", "Trials of your own making.",
                "Line shown under the name on the game-mode button.");

            // Hang_04 is the Grand Forum. Identified by scanning every shipped scene
            // bundle: Hang_04 holds the room (tilemap, doors left1/right1) and loads
            // Hang_04_boss additively on top of it, which is where the three
            // BattleScenes, the twelve waves and all the "FORUM BATTLE START" /
            // "Forum Battler" / "GARMOND_GRAND_FORUM" wiring live.
            GauntletScene = cfg.Bind(
                "Room", "GauntletScene", "Hang_04",
                "The gauntlet room. Its own BattleScene is reused to run your waves, and the home " +
                "point below is where you start. If this is wrong, stand in the right room and use " +
                "F9 -> 'Use this room as the gauntlet'.");

            // "Home point" is the right-hand end of the same room - where you spawn
            // and where the bench goes. It is NOT a separate scene; that was the
            // wrong model and is why the first version sent you somewhere else.
            // Defaults are the spot in Hang_04 you picked: the quiet right-hand end,
            // clear of the arena. Pinned by default so the automatic placement - which
            // is only ever a guess about a room's shape - never gets a say.
            HomePointSet = cfg.Bind(
                "Room", "HomePointSet", true,
                "Use the exact HomePointX/Y below instead of auto-placing. The dev menu's " +
                "'Set home point here' sets all three at once.");

            HomePointX = cfg.Bind(
                "Room", "HomePointX", 53.5f,
                "Where you spawn and where the bench goes. Only used when HomePointSet is true; " +
                "otherwise the mod places it to the right of the arena automatically.");

            HomePointY = cfg.Bind(
                "Room", "HomePointY", 4.6f,
                "See HomePointX.");

            AddBench = cfg.Bind(
                "Room", "AddBench", true,
                "Put a bench at the home point if the room hasn't already got one.");


            BenchYOffset = cfg.Bind(
                "Room", "BenchYOffset", 0f,
                "Extra nudge up or down for the bench. The mod already measures the bench's own " +
                "artwork and rests it on the floor, so this is only needed if you want it deliberately " +
                "raised or sunk.");

            // Hang_06b is in the same area as the arena, so its bench matches the
            // room's architecture instead of looking imported. It's also the smallest
            // candidate (~1.9 MB) and, crucially, has no unlock popup: Bellway "bell
            // benches" are shrines that animate in the first time you reach a station,
            // and a cloned one shows the popup rather than a bench. The Cradle bench
            // has no popup either but is a silk sack, which looked out of place here.
            BenchDonorScene = cfg.Bind(
                "Room", "BenchDonorScene", "Hang_06b",
                "Scene the bench is lifted from. It's loaded additively once and then left " +
                "loaded but deactivated, because its asset bundle owns the bench's art. " +
                "Pick one from the same area as your arena so it matches, and avoid the Bellway " +
                "rooms - their benches are shrines with an unlock popup.");

            StripArena = cfg.Bind(
                "Behaviour", "StripArena", true,
                "Clear the rooms' stock enemies and hazards so only your waves fight you.");

            // Off by default. Disabling door colliders to keep you in the arena turned
            // out to cause more confusion than it prevented - Hang_04's two doors are
            // the room's only way in or out, and a mod interfering with them is
            // indistinguishable from the room being broken.
            SealExits = cfg.Bind(
                "Behaviour", "SealExits", false,
                "Disable the room's door colliders so you can't wander out of the gauntlet. Off by " +
                "default - the mod leaves the room's own connections completely alone.");

            KeepRoomClear = cfg.Bind(
                "Behaviour", "KeepRoomClear", true,
                "While you're in the gauntlet, keep deleting any enemy the mod didn't spawn. " +
                "Needed because the room's own FSMs can bring the stock fight in when you cross the " +
                "trigger, i.e. after every setup pass has already run.");

            RescueOutOfBounds = cfg.Bind(
                "Behaviour", "RescueOutOfBounds", true,
                "If you end up below the floor or outside the room - which some death and respawn " +
                "paths can do in a room the mod has rearranged - put you back at the home point.");

            SpawnAtArenaCentre = cfg.Bind(
                "Behaviour", "SpawnAtArenaCentre", true,
                "Spawn every wave enemy in the middle of the arena. Turn off to spread them across " +
                "the fighting area and reuse the room's own enemy positions - prettier, but some can " +
                "end up out of sight.");

            ToolsBetweenFights = cfg.Bind(
                "Behaviour", "ToolsBetweenFights", true,
                "Let you change tools and crests anywhere in the gauntlet room while no fight is " +
                "running. Uses the game's own CheatManager.CanChangeEquipsAnywhere switch, and is " +
                "turned back off the moment the waves start.");

            // The arena plays a short intro when a fight begins - the camera locks,
            // the gates slam and there's a roar. Enemies arriving during that get
            // hidden behind it, so wave one waits for it to finish.
            FirstWaveDelay = cfg.Bind(
                "Behaviour", "FirstWaveDelay", 2.5f,
                "Seconds to hold wave one back, so enemies appear after the fight's opening flourish " +
                "rather than during it. Added on top of that wave's own start delay.");

            ShowClearMessage = cfg.Bind(
                "Behaviour", "ShowClearMessage", false,
                "Show a title card when a run is cleared. Off by default - it interrupts the game's " +
                "own end-of-fight moment, which is nicer without it.");

            // Some enemies burrow, and start their emerge animation from wherever they
            // were placed. Put one exactly on the floor and it can come up inside it -
            // hence a little clearance rather than none.
            SpawnHeight = cfg.Bind(
                "Behaviour", "SpawnHeight", 1.6f,
                "How far above the floor wave enemies appear. Raise it if anything spawns stuck in " +
                "the ground.");

            HarvestEnemies = cfg.Bind(
                "Behaviour", "HarvestEnemies", true,
                "Bank a copy of every enemy you encounter so it can be used in a wave. " +
                "This is the only way enemies become available - Silksong doesn't ship them as loadable prefabs.");

            ArenaRespawnMarker = cfg.Bind(
                "Behaviour", "RespawnMarkerName", "",
                "Name of the RespawnMarker to drop Hornet at. Leave blank to auto-pick (recommended - " +
                "marker names differ per scene).");

            ShowModeMenuOnNewGame = cfg.Bind(
                "Mode", "ShowModeMenuOnNewGame", true,
                "Route 'New Game' through the game-mode menu so the Custom Gauntlet option is offered. " +
                "Turn off to restore stock behaviour.");

            MaxLoadoutOnStart = cfg.Bind(
                "Mode", "MaxLoadoutOnStart", true,
                "Apply the full loadout - max stats, all abilities, and every red/blue/yellow tool.");

            SkipStartupLogos = cfg.Bind(
                "Startup", "SkipStartupLogos", true,
                "Skip the Pre_Menu_Intro logo/splash sequence and go straight to the title menu. " +
                "(The new-game opening cutscene is skipped by the gauntlet start path regardless of this.)");

            // Which save slots are gauntlet runs. Without this, continuing a normal
            // save that happens to be parked in the staging room would re-arm the
            // mode and strip a real playthrough's room.
            GauntletProfileIds = cfg.Bind(
                "Bookkeeping", "GauntletProfileIds", "",
                "Comma-separated save slots created as Custom Gauntlet runs. Managed automatically; " +
                "clear it to make the mod forget which saves are gauntlets.");

            PanelKey = cfg.Bind(
                "Keys", "WavePanelKey", KeyCode.F7,
                "Opens the wave editor.");

            DevMenuKey = cfg.Bind(
                "Keys", "DevMenuKey", KeyCode.F9,
                "Opens the small dev menu (loadout, scene inspection, manual jumps).");

            FreeMoveKey = cfg.Bind(
                "Keys", "FreeMoveKey", KeyCode.Alpha1,
                "Toggles free move: gravity and collision off, fly with arrows/WASD, shift for speed.");


            ZoomOutKey = cfg.Bind(
                "Keys", "ZoomOutKey", KeyCode.Minus,
                "Zooms the camera out, up to 8x.");

            ZoomInKey = cfg.Bind(
                "Keys", "ZoomInKey", KeyCode.Equals,
                "Zooms back in. Returns the camera to the game at 1x.");

            ReadoutKey = cfg.Bind(
                "Keys", "ReadoutKey", KeyCode.Alpha2,
                "Toggles the mod's own masks/silk readout, for when the game's HUD doesn't come up.");

            FreeMoveSpeed = cfg.Bind(
                "Keys", "FreeMoveSpeed", 14f,
                "Free move speed in world units per second. Shift triples it.");
        }
    }
}
