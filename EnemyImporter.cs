using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// Brings enemies in from any room in the game.
    ///
    /// Load a room, take copies of its enemies, keep the room parked out of sight.
    ///
    /// Two things about Silksong shape this, both learned the hard way:
    ///
    /// 1. **The room has to stay loaded.** An earlier version unloaded it with
    ///    autoReleaseHandle: false, on the belief that retaining the handle pinned the
    ///    asset bundle. It doesn't - that flag only controls whether the handle
    ///    *wrapper* is recycled. Unloading a scene unloads its bundle, and that
    ///    destroys the serialised data behind the cloned enemies' sprites and meshes
    ///    even though they've been moved to a persistent scene. That was the invisible
    ///    enemies. So the room stays loaded, offset far away and switched off.
    ///
    /// 2. **Enemies are mostly pooled, not placed.** Only a few rooms sit their
    ///    enemies in the scene; most carry a PersonalObjectPool listing prefabs and
    ///    build instances at runtime. Reading the prefab list means the room never has
    ///    to run - which matters, because a room that runs is a room that draws itself
    ///    over the arena.
    ///
    /// The proper fix for all of this is Silksong.AssetHelper, which repacks individual
    /// scene assets into their own bundle so no scene load is needed at all. It isn't
    /// installed here; if it ever is, this class is what it replaces.
    /// </summary>
    internal static class EnemyImporter
    {
        /// <summary>
        /// Enemy-dense rooms spanning the whole game. Any scene name can be typed in
        /// instead; these are just a good spread to work through.
        /// </summary>
        public static readonly string[] Suggested =
        {
            "Bone_06", "Bone_18", "Bone_East_10",
            "Greymoor_04", "Greymoor_20c",
            "Shellwood_03", "Shellwood_11",
            "Dock_08", "Dock_03c",
            "Coral_11", "Coral_Judge_Arena",
            "Song_04", "Song_18",
            "Library_02", "Hang_04",
            "Under_18", "Under_10",
            "Cog_05", "Cog_08",
            "Ant_04_mid", "Ant_20",
            "Dust_03", "Dust_05", "Dust_chef",
            "Clover_04b", "Clover_10_web",
            "Crawl_09", "Peak_04", "Ward_09",
            "Abyss_09", "Aqueduct_05", "Wisp_04",
        };

        /// <summary>
        /// Rooms we're holding onto, one handle each. They stay loaded because their
        /// bundles own the artwork of the enemies cloned out of them; releasing one
        /// would blank every enemy that came from it.
        /// </summary>
        private static readonly List<AsyncOperationHandle<SceneInstance>> Parked =
            new List<AsyncOperationHandle<SceneInstance>>();

        private const int MaxRoomsPerPass = 6;

        /// <summary>Rooms already searched this session, so repeat presses make progress.</summary>
        private static readonly HashSet<string> Searched = new HashSet<string>();

        public static bool Busy { get; private set; }
        public static string LastResult { get; private set; } = "";
        public static int ParkedRoomCount => Parked.Count;

        // ------------------------------------------------------------------

        /// <summary>
        /// Works through rooms until every enemy the waves ask for exists, or until it
        /// runs out of rooms to try.
        ///
        /// By elimination, not by lookup: there's no mapping from a Hunter's Journal
        /// entry to the room it lives in - journal names are display names and the
        /// scene bundles are compressed, so scanning them offline finds nothing for
        /// most enemies. Import a room, see whether the missing list shrank, stop the
        /// moment it's empty.
        /// </summary>
        public static IEnumerator ImportMissing()
        {
            List<string> wanted = WaveBuilder.MissingEnemies();
            if (wanted.Count == 0)
            {
                LastResult = "Nothing missing - every enemy in your waves is ready.";
                yield break;
            }

            Plugin.Log.LogInfo($"EnemyImporter: looking for {string.Join(", ", wanted.ToArray())}");

            int roomsTried = 0;

            foreach (string scene in Suggested)
            {
                if (!WaveBuilder.HasMissingEnemies) break;
                if (Searched.Contains(scene)) continue;

                // Each import pins a bundle for the rest of the session, so a single
                // pass is capped rather than pulling in all thirty rooms at once.
                if (roomsTried >= MaxRoomsPerPass)
                {
                    Plugin.Log.LogInfo($"EnemyImporter: pausing after {MaxRoomsPerPass} rooms - press again to keep looking.");
                    break;
                }

                while (Busy) yield return null;

                roomsTried++;
                Searched.Add(scene);
                yield return Import(scene);
            }

            List<string> stillMissing = WaveBuilder.MissingEnemies();
            int left = 0;
            foreach (string scene in Suggested) if (!Searched.Contains(scene)) left++;

            LastResult = stillMissing.Count == 0
                ? $"All enemies ready. Library holds {EnemyLibrary.Count}."
                : left > 0
                    ? $"Searched {roomsTried} more room(s); still missing {string.Join(", ", stillMissing.ToArray())}. " +
                      $"{left} room(s) left to try - press again."
                    : $"Searched every room I know of; couldn't find: {string.Join(", ", stillMissing.ToArray())}.";

            Plugin.Log.LogInfo("EnemyImporter: " + LastResult);
        }

        public static IEnumerator Import(string sceneName)
        {
            if (Busy) { LastResult = "Still importing the last one."; yield break; }
            if (string.IsNullOrEmpty(sceneName)) { LastResult = "No scene name given."; yield break; }

            if (!SceneCatalog.Exists(sceneName))
            {
                LastResult = $"'{sceneName}' isn't a scene in this build.";
                Plugin.Log.LogWarning("EnemyImporter: " + LastResult);
                yield break;
            }

            Scene alreadyHere = SceneManager.GetSceneByName(sceneName);
            if (alreadyHere.IsValid() && alreadyHere.isLoaded)
            {
                int already = EnemyLibrary.HarvestScene(alreadyHere);
                LastResult = $"'{sceneName}' was already loaded - banked {already} new enemy type(s).";
                yield break;
            }

            Busy = true;
            SceneColorManager arenaColour = CaptureArenaColour();

            AsyncOperationHandle<SceneInstance> handle;
            try
            {
                handle = Addressables.LoadSceneAsync("Scenes/" + sceneName, LoadSceneMode.Additive, activateOnLoad: true);
            }
            catch (Exception e)
            {
                Busy = false;
                LastResult = $"Couldn't start loading '{sceneName}'.";
                Plugin.Log.LogError("EnemyImporter: " + LastResult + " " + e);
                yield break;
            }

            yield return handle;

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Busy = false;
                LastResult = $"'{sceneName}' failed to load.";
                Plugin.Log.LogError("EnemyImporter: " + LastResult);
                yield break;
            }

            Scene donor = handle.Result.Scene;

            // Hide it the instant it exists. It occupies the same world coordinates as
            // the room you're standing in, so anything left active is drawn straight
            // through the arena.
            try { Hide(donor); } catch (Exception e) { Plugin.Log.LogWarning("EnemyImporter: hide failed: " + e.Message); }

            yield return null;

            int added = 0;
            try
            {
                BenchImporter.ScrubHijackableTags(donor);

                // Harvesting works on inactive objects, so hiding first costs nothing.
                added = EnemyLibrary.HarvestScene(donor);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("EnemyImporter: harvest failed: " + e);
            }

            if (added > 0)
            {
                // Keep it. Unloading would take the enemies' artwork with it.
                Parked.Add(handle);
                Plugin.Log.LogInfo(
                    $"EnemyImporter: '{sceneName}' parked and kept loaded - its bundle owns the art of " +
                    $"what we just took ({Parked.Count} room(s) held).");
            }
            else
            {
                // Nothing worth keeping: release it properly and reclaim the memory.
                yield return ReleaseRoom(handle, sceneName);
            }

            RestoreArenaColour(arenaColour);

            Busy = false;
            LastResult = $"Banked {added} enemy type(s) from '{sceneName}'. Library holds {EnemyLibrary.Count}.";
            Plugin.Log.LogInfo("EnemyImporter: " + LastResult);
        }

        // ------------------------------------------------------------------

        private static IEnumerator ReleaseRoom(AsyncOperationHandle<SceneInstance> handle, string sceneName)
        {
            AsyncOperationHandle<SceneInstance> unload;
            try
            {
                unload = Addressables.UnloadSceneAsync(handle, autoReleaseHandle: true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"EnemyImporter: couldn't unload '{sceneName}': {e.Message}");
                yield break;
            }

            yield return unload;
            Plugin.Log.LogInfo($"EnemyImporter: '{sceneName}' released - nothing was taken from it.");
        }

        /// <summary>The colour manager the arena is currently using.</summary>
        private static SceneColorManager CaptureArenaColour()
        {
            GameCameras cameras = GameCameras.instance;
            return cameras != null ? cameras.sceneColorManager : null;
        }

        /// <summary>
        /// Puts the arena's palette back. An imported room's own colour manager takes
        /// over the moment it loads, and unloading the room doesn't undo it - so
        /// without this the arena keeps whichever area's tint it borrowed.
        /// </summary>
        private static void RestoreArenaColour(SceneColorManager arenaColour)
        {
            try
            {
                GameCameras cameras = GameCameras.instance;
                if (cameras == null) return;

                if (arenaColour != null && cameras.sceneColorManager != arenaColour)
                {
                    cameras.sceneColorManager = arenaColour;
                }

                if (cameras.sceneColorManager != null)
                {
                    cameras.sceneColorManager.SceneInit();
                    cameras.sceneColorManager.UpdateScript(forceUpdate: true);
                    Plugin.Log.LogInfo("EnemyImporter: restored the arena's colour.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("EnemyImporter: couldn't restore the arena's colour: " + e.Message);
            }
        }

        /// <summary>Everything off and moved far away, for the brief window before unload.</summary>
        private static void Hide(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                root.SetActive(false);
                root.transform.position += new Vector3(100000f, 0f, 0f);
            }
        }
    }
}
