using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// Room surgery: clearing out the stock fight, sealing the gauntlet off from the
    /// rest of the game, working out which room is next door, and finding somewhere
    /// solid to stand.
    ///
    /// Everything here is guarded - a failure logs and moves on rather than leaving
    /// you in a half-built room.
    /// </summary>
    internal static class ArenaSetup
    {
        // ------------------------------------------------------------------
        // What counts as "the room"
        // ------------------------------------------------------------------

        /// <summary>
        /// The Grand Forum is built from two scenes at once: Hang_04 holds the
        /// tilemap, doors and geometry, and Hang_04_boss is loaded additively on top
        /// of it carrying the BattleScenes and all twelve waves. Filtering on "is
        /// this the active scene" would therefore miss the entire arena.
        ///
        /// So the rule is the other way round: everything loaded is part of the room
        /// unless it's persistent (DontDestroyOnLoad) or the deactivated scene we
        /// borrowed the bench from.
        /// </summary>
        public static bool IsRoomScene(Scene scene)
        {
            if (!scene.IsValid()) return false;
            if (scene.name == "DontDestroyOnLoad") return false;
            if (scene.name == GauntletConfig.BenchDonorScene.Value) return false;

            return true;
        }

        public static bool IsRoomObject(GameObject go)
        {
            return go != null && IsRoomScene(go.scene);
        }

        /// <summary>Names of every scene currently making up the room, for logging.</summary>
        public static string DescribeLoadedScenes()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) names.Add(s.name);
            }
            return string.Join(" + ", names.ToArray());
        }

        // ------------------------------------------------------------------
        // Clearing
        // ------------------------------------------------------------------

        /// <summary>
        /// Removes the room's own fight. Spawners go first, or a battle trigger can
        /// repopulate the room a second after we've cleared it.
        ///
        /// Note this does NOT remove BattleScene components: WaveBuilder reuses the
        /// gauntlet's BattleScene to run your waves, so killing it here would take
        /// the camera locks, gates and music with it.
        /// </summary>
        /// <param name="includeInactive">
        /// Also destroy enemies that are currently switched off.
        ///
        /// This is the important one. A gauntlet's enemies sit in the room *inactive*
        /// until their wave turns them on, so a strip that only touches active objects
        /// leaves the entire stock fight sitting there waiting - which is exactly what
        /// "I stepped in and got the normal enemies" was. Whatever starts the fight
        /// then switches them on and we never get a say.
        ///
        /// It's off by default because inactive enemies elsewhere are usually object
        /// pool members, and deleting out of a pool breaks its bookkeeping. In a room
        /// we've deliberately taken over, that trade is worth making.
        /// </param>
        public static void StripRoom(bool includeInactive = false)
        {
            if (!GauntletConfig.StripArena.Value) return;

            Scene scene = SceneManager.GetActiveScene();
            FindObjectsInactive inactive = includeInactive
                ? FindObjectsInactive.Include
                : FindObjectsInactive.Exclude;

            try
            {
                int spawners = DestroyComponents<EnemySpawner>(scene)
                               + DestroyComponents<BossSceneController>(scene);

                int enemies = 0;
                foreach (HealthManager hm in UnityEngine.Object.FindObjectsByType<HealthManager>(
                             inactive, FindObjectsSortMode.None))
                {
                    if (hm == null || !IsStrippable(hm.gameObject, scene, includeInactive)) continue;
                    UnityEngine.Object.Destroy(hm.gameObject);
                    enemies++;
                }

                int hazards = 0;
                foreach (DamageHero dh in UnityEngine.Object.FindObjectsByType<DamageHero>(
                             inactive, FindObjectsSortMode.None))
                {
                    // May already be gone as a child of an enemy we just destroyed.
                    if (dh == null || !IsStrippable(dh.gameObject, scene, includeInactive)) continue;
                    UnityEngine.Object.Destroy(dh.gameObject);
                    hazards++;
                }

                Plugin.Log.LogInfo(
                    $"Custom Gauntlet: cleared {enemies} enemies, {hazards} hazards and " +
                    $"{spawners} spawner(s) from '{scene.name}'" +
                    (includeInactive ? " (including dormant ones)." : "."));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: strip failed: " + e);
            }
        }

        private static int DestroyComponents<T>(Scene scene) where T : Component
        {
            int count = 0;
            foreach (T component in UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                if (component == null || !IsRoomObject(component.gameObject)) continue;
                UnityEngine.Object.Destroy(component);
                count++;
            }
            return count;
        }

        private static bool IsStrippable(GameObject go, Scene scene, bool includeInactive)
        {
            if (go == null) return false;

            // Objects outside the room are the persistent managers and the global
            // object pool - deleting out of those breaks the pool's bookkeeping.
            if (!IsRoomScene(go.scene)) return false;

            if (!includeInactive && !go.activeInHierarchy) return false;

            HeroController hero = HeroController.instance;
            if (hero != null && go.transform.IsChildOf(hero.transform)) return false;

            // Never eat our own waves.
            if (WaveBuilder.IsOurs(go)) return false;

            return true;
        }

        // ------------------------------------------------------------------
        // Sealing
        // ------------------------------------------------------------------

        /// <summary>
        /// Disables the room's doors so the gauntlet stays walled off from the rest
        /// of Pharloom. <paramref name="keepDoorTo"/> leaves one open if you ever need
        /// a way out to a specific scene.
        /// </summary>
        public static void SealExits(string keepDoorTo = null)
        {
            if (!GauntletConfig.SealExits.Value) return;

            Scene scene = SceneManager.GetActiveScene();

            try
            {
                int sealedCount = 0;
                int kept = 0;

                foreach (TransitionPoint tp in UnityEngine.Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None))
                {
                    if (tp == null || !IsRoomObject(tp.gameObject)) continue;

                    bool keep = !string.IsNullOrEmpty(keepDoorTo) && tp.targetScene == keepDoorTo;

                    // Toggle colliders rather than the object: the door visuals stay,
                    // and the TransitionPoint stays registered so the game's own
                    // "fall back to any available gate" spawn logic still works.
                    bool changed = false;
                    foreach (Collider2D col in tp.GetComponentsInChildren<Collider2D>(true))
                    {
                        if (col.enabled == keep) continue;
                        col.enabled = keep;
                        changed = true;
                    }

                    if (keep) kept++;
                    else if (changed) sealedCount++;
                }

                if (sealedCount > 0 || kept > 0)
                {
                    Plugin.Log.LogInfo(
                        $"Custom Gauntlet: sealed {sealedCount} exit(s) in '{scene.name}'" +
                        (kept > 0 ? $", left {kept} open to '{keepDoorTo}'." : "."));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: seal failed: " + e);
            }
        }

        /// <summary>What the room's doors currently lead to, for the dev menu.</summary>
        public static string DescribeDoorTargets()
        {
            var seen = new List<string>();

            foreach (TransitionPoint tp in UnityEngine.Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None))
            {
                if (tp == null || !IsRoomObject(tp.gameObject)) continue;

                string entry = $"{tp.name}->{(string.IsNullOrEmpty(tp.targetScene) ? "(none)" : tp.targetScene)}";
                if (!seen.Contains(entry)) seen.Add(entry);
            }

            return seen.Count == 0 ? "no doors here" : string.Join(", ", seen.ToArray());
        }

        /// <summary>
        /// Releases the arena's own containment: the camera lock and the battle gates.
        ///
        /// A BattleScene locks the camera and slams its gates when a fight starts, and
        /// only lets go in its own end sequence. Resetting a fight from outside skips
        /// that, so the lock can outlive the battle - which feels exactly like being
        /// sealed in one half of the room with the doors going nowhere.
        /// </summary>
        public static void ReleaseArenaLocks()
        {
            try
            {
                BattleScene battle = WaveBuilder.CurrentBattle;
                if (battle == null) return;

                if (battle.camLocks != null && battle.camLocks.activeSelf)
                {
                    battle.camLocks.SetActive(false);
                    Plugin.Log.LogInfo("Custom Gauntlet: released the arena's camera lock.");
                }

                battle.SendEventToChildren("BG OPEN");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: couldn't release the arena locks: " + e.Message);
            }
        }

        /// <summary>Opens the room's doors again - used once the gauntlet is beaten.</summary>
        public static void UnsealExits()
        {
            try
            {
                int opened = 0;
                foreach (TransitionPoint tp in UnityEngine.Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None))
                {
                    if (tp == null || !IsRoomObject(tp.gameObject)) continue;

                    foreach (Collider2D col in tp.GetComponentsInChildren<Collider2D>(true))
                    {
                        if (col.enabled) continue;
                        col.enabled = true;
                        opened++;
                    }
                }

                if (opened > 0) Plugin.Log.LogInfo($"Custom Gauntlet: gauntlet beaten - opened {opened} door collider(s).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: couldn't open the doors: " + e);
            }
        }

        // ------------------------------------------------------------------
        // Home point and teleports
        // ------------------------------------------------------------------

        /// <summary>
        /// The right-hand end of the room: where you spawn and where the bench goes.
        ///
        /// The gauntlet and the bench area are two ends of the *same* scene, so this
        /// is a position, not a room. If you've pinned one with the dev menu that
        /// wins; otherwise it's placed in the gap between the right edge of the
        /// battle trigger and the right edge of the room, which is exactly the strip
        /// of floor the arena doesn't use.
        /// </summary>
        public static Vector3 HomePoint()
        {
            TryHomePoint(out Vector3 point);
            return point;
        }

        /// <summary>
        /// Works out the home point and says whether it's trustworthy.
        ///
        /// "Trustworthy" means a pinned point, or a computed one that both found
        /// floor underneath it and sits inside the room. Anything else is a guess in
        /// a room whose shape we've misjudged, and acting on a guess is how you end
        /// up out of bounds - so the caller is told and can leave Hornet alone.
        /// </summary>
        public static bool TryHomePoint(out Vector3 point)
        {
            if (GauntletConfig.HomePointSet.Value)
            {
                point = new Vector3(GauntletConfig.HomePointX.Value, GauntletConfig.HomePointY.Value, 0f);
                return true;
            }

            float roomRight = RoomWidth();
            float x;

            Bounds? arena = ArenaBounds();
            if (arena.HasValue)
            {
                float leftLimit = arena.Value.max.x + 3f;
                float rightLimit = Mathf.Max(leftLimit, roomRight - 4f);
                x = Mathf.Lerp(leftLimit, rightLimit, 0.5f);
            }
            else
            {
                x = Mathf.Max(4f, roomRight - 6f);
            }

            point = SnapToGround(new Vector3(x, RoomHeight() - 1f, 0f), out bool foundFloor);

            if (!foundFloor)
            {
                // Keep Hornet's current height rather than dropping her in from the
                // ceiling, but flag it as unreliable.
                HeroController hero = HeroController.instance;
                point.y = hero != null ? hero.transform.position.y : RoomHeight() * 0.5f;
                Plugin.Log.LogWarning(
                    $"Custom Gauntlet: no floor under x={x:0.#} in this room, so the home point is a guess. " +
                    "Stand where you want to start and use F9 -> 'Set home point here'.");
                return false;
            }

            return IsInsideRoom(point);
        }

        private static bool IsInsideRoom(Vector3 point)
        {
            float w = RoomWidth();
            float h = RoomHeight();
            bool inside = point.x >= 0f && point.x <= w && point.y >= 0f && point.y <= h;

            if (!inside)
            {
                Plugin.Log.LogWarning(
                    $"Custom Gauntlet: computed home point ({point.x:0.#}, {point.y:0.#}) is outside the " +
                    $"room ({w:0.#} x {h:0.#}); leaving Hornet where she is.");
            }

            return inside;
        }

        /// <summary>
        /// The fighting area as a box. Taken from the battle trigger when there is
        /// one, since the game sizes that collider to the arena; otherwise everything
        /// in the room left of the home point, which is where the arena is.
        /// </summary>
        public static Bounds ArenaArea()
        {
            Bounds? fromTrigger = ArenaBounds();
            if (fromTrigger.HasValue) return fromTrigger.Value;

            Vector3 home = HomePoint();
            float left = 2f;
            float right = Mathf.Max(left + 4f, home.x - 4f);
            float height = Mathf.Max(8f, RoomHeight() * 0.6f);

            var centre = new Vector3((left + right) * 0.5f, home.y + height * 0.35f, 0f);
            return new Bounds(centre, new Vector3(right - left, height, 4f));
        }

        /// <summary>Middle of the fighting area - where "teleport to arena" puts you.</summary>
        public static Vector3 ArenaPoint()
        {
            Bounds? arena = ArenaBounds();
            if (arena.HasValue) return SnapToGround(new Vector3(arena.Value.center.x, arena.Value.max.y, 0f));

            // No trigger to aim at: fall back to the middle of the room.
            return SnapToGround(new Vector3(RoomWidth() * 0.5f, RoomHeight() - 1f, 0f));
        }

        /// <summary>
        /// The battle trigger's bounds. That collider is sized to the fighting area,
        /// so it's the most reliable description of "where the gauntlet is" available
        /// without hand-measuring the room.
        /// </summary>
        private static Bounds? ArenaBounds()
        {
            BattleScene battle = WaveBuilder.CurrentBattle;
            if (battle == null) return null;

            Bounds? result = null;
            foreach (Collider2D col in battle.GetComponents<Collider2D>())
            {
                if (!col.isTrigger) continue;

                // Collider2D.bounds is only meaningful while the collider is enabled,
                // and the battle disables its own trigger as soon as a fight starts.
                // Reading it disabled gives a degenerate box at the origin, which
                // would put every spawn point in the bottom-left corner of the room.
                bool wasEnabled = col.enabled;
                if (!wasEnabled) col.enabled = true;

                Bounds b = col.bounds;

                if (!wasEnabled) col.enabled = false;

                if (b.size.x < 0.5f && b.size.y < 0.5f) continue;

                if (result.HasValue)
                {
                    Bounds grown = result.Value;
                    grown.Encapsulate(b);
                    result = grown;
                }
                else
                {
                    result = b;
                }
            }

            return result;
        }

        private static float RoomWidth()
        {
            GameManager gm = GameManager.instance;
            return gm != null && gm.sceneWidth > 1f ? gm.sceneWidth : 60f;
        }

        private static float RoomHeight()
        {
            GameManager gm = GameManager.instance;
            return gm != null && gm.sceneHeight > 1f ? gm.sceneHeight : 30f;
        }

        /// <summary>Drops Hornet at a point, killing any inherited momentum.</summary>
        public static void MoveHeroTo(Vector3 point, string why)
        {
            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: no hero to move.");
                return;
            }

            try
            {
                Vector3 target = SnapToGround(point) + new Vector3(0f, 0.6f, 0f);
                target.z = hero.transform.position.z;
                hero.transform.position = target;

                Rigidbody2D body = hero.GetComponent<Rigidbody2D>();
                if (body != null) body.linearVelocity = Vector2.zero;

                Plugin.Log.LogInfo($"Custom Gauntlet: moved Hornet to {target} ({why}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: couldn't move the hero: " + e);
            }
        }

        /// <summary>
        /// Catches Hornet if she ends up somewhere she shouldn't be, and puts her back
        /// at the home point.
        ///
        /// Deaths don't always reload the room - hazard respawns and same-scene
        /// respawns skip it - so the "move to the home point on load" rule doesn't
        /// always get a turn. Rather than chase every respawn path, this just checks
        /// where she actually is: below the floor or outside the room means something
        /// went wrong, whatever the cause.
        /// </summary>
        public static bool RescueIfOutOfBounds()
        {
            HeroController hero = HeroController.instance;
            if (hero == null) return false;

            Vector3 at = hero.transform.position;

            bool belowWorld = at.y < -6f;
            bool outsideRoom = !IsInRoomBounds(at + new Vector3(0f, 2f, 0f));

            if (!belowWorld && !outsideRoom) return false;

            Plugin.Log.LogWarning(
                $"Custom Gauntlet: Hornet was at ({at.x:0.#}, {at.y:0.#}), outside the room - " +
                "putting her back at the home point.");

            MoveHeroTo(HomePoint(), "out-of-bounds rescue");
            return true;
        }

        /// <summary>Records the hero's current spot as the home point.</summary>
        public static void PinHomePointHere()
        {
            HeroController hero = HeroController.instance;
            if (hero == null) return;

            Vector3 at = hero.transform.position;
            GauntletConfig.HomePointX.Value = at.x;
            GauntletConfig.HomePointY.Value = at.y;
            GauntletConfig.HomePointSet.Value = true;

            Plugin.Log.LogInfo($"Custom Gauntlet: home point pinned at ({at.x:0.##}, {at.y:0.##}).");
        }

        // ------------------------------------------------------------------
        // Spawning and benches
        // ------------------------------------------------------------------

        /// <summary>
        /// Notes down whichever respawn marker this room actually has and saves it,
        /// so deaths land here instead of falling back to the game's default.
        /// </summary>
        public static void RecordSpawnMarker()
        {
            try
            {
                Transform spawn = FindSafeSpawn();
                if (spawn == null) return;

                GauntletMode.ResolvedMarkerName = spawn.name;

                PlayerData pd = PlayerData.instance;
                if (pd != null)
                {
                    pd.respawnMarkerName = spawn.name;
                    pd.respawnScene = SceneManager.GetActiveScene().name;
                    pd.respawnType = 0;
                }

                // Hazard deaths ignore respawn markers entirely and use this instead,
                // so it has to be set explicitly or a pit death sends you to (0,0).
                TryHomePoint(out Vector3 home);
                pd.hazardRespawnLocation = home;

                Plugin.Log.LogInfo(
                    $"Custom Gauntlet: respawn marker resolved to '{spawn.name}', " +
                    $"hazard respawn pinned to ({home.x:0.#}, {home.y:0.#}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: marker record failed: " + e);
            }
        }

        /// <summary>
        /// Best available place to put Hornet, in descending order of correctness.
        /// Used for the initial drop-in and as the safety net in
        /// HeroController.LocateSpawnPoint.
        /// </summary>
        public static Transform FindSafeSpawn()
        {
            RestBench[] benches = UnityEngine.Object.FindObjectsByType<RestBench>(FindObjectsSortMode.None);

            // A bench's own respawn marker is the right answer whenever there's a
            // bench: it's the spot the game itself would send you to after resting,
            // so dying and resting put you in the same place.
            List<RespawnMarker> markers = RespawnMarker.Markers;
            if (markers != null && benches.Length > 0)
            {
                foreach (RespawnMarker marker in markers)
                {
                    if (!IsUsableMarker(marker)) continue;

                    foreach (RestBench bench in benches)
                    {
                        if (bench == null) continue;
                        if (marker.transform.IsChildOf(bench.transform.root)
                            && Vector3.Distance(marker.transform.position, bench.transform.position) < 6f)
                        {
                            return marker.transform;
                        }
                    }
                }
            }

            if (markers != null)
            {
                foreach (RespawnMarker marker in markers)
                {
                    if (IsUsableMarker(marker)) return marker.transform;
                }
            }

            foreach (RestBench bench in benches)
            {
                if (bench != null && !IsHeroOwned(bench.gameObject)) return bench.transform;
            }

            List<TransitionPoint> gates = TransitionPoint.TransitionPoints;
            if (gates != null && gates.Count > 0) return gates[0].transform;

            return null;
        }

        /// <summary>
        /// The hero prefab carries a RespawnMarker of its own, so an unfiltered search
        /// happily returns Hornet as her own spawn point. That sends the respawn
        /// sequence down the wrong branch - which, among other things, is the branch
        /// that slides the HUD back in.
        /// </summary>
        private static bool IsUsableMarker(RespawnMarker marker)
        {
            if (marker == null || !marker.gameObject.activeInHierarchy) return false;
            return !IsHeroOwned(marker.gameObject);
        }

        private static bool IsHeroOwned(GameObject go)
        {
            HeroController hero = HeroController.instance;
            if (hero == null) return false;
            return go.transform.IsChildOf(hero.transform) || go == hero.gameObject;
        }

        /// <summary>
        /// Puts the mask/silk HUD back on screen.
        ///
        /// Sending the slide-in event isn't enough on its own, which is why the first
        /// two attempts at this failed. The HUD lives on a separate camera that
        /// GameCameras.StartScene switches on when a *gameplay* scene begins, and the
        /// gauntlet's start route can miss that: no HUD camera means no HUD, however
        /// many times you tell the canvas to slide in.
        ///
        /// So this redoes the gameplay branch of StartScene by hand - move the menu
        /// canvas onto the HUD camera, spawn the map, put it in gameplay mode, make
        /// sure the camera and its gameplay child are actually active - and only then
        /// sends the slide-in.
        /// </summary>
        public static void ShowHud(bool verbose = false)
        {
            try
            {
                GameCameras cameras = GameCameras.instance;
                if (cameras == null)
                {
                    if (verbose) Plugin.Log.LogWarning("Custom Gauntlet: no GameCameras yet.");
                    return;
                }

                if (cameras.hudCamera == null)
                {
                    Plugin.Log.LogWarning("Custom Gauntlet: GameCameras has no hudCamera - can't show the HUD.");
                    return;
                }

                if (!cameras.hudCamera.gameObject.activeSelf) cameras.hudCamera.gameObject.SetActive(true);
                if (!cameras.hudCamera.enabled) cameras.hudCamera.enabled = true;

                HUDCamera hud = cameras.hudCamera.GetComponent<HUDCamera>();
                if (hud != null) hud.SetIsGameplayMode(isGameplayMode: true);

                // The actual culprit, most likely: a HUD camera can be active, enabled
                // and reporting itself visible while rendering absolutely nothing,
                // because MoveMenuToMainCamera sets its culling mask to 0. Nothing
                // about the camera's state gives that away - and it would defeat any
                // other mod's HUD toggle too, since there's nothing to toggle.
                //
                // MoveMenuToHUDCamera is the game's own repair for that: it points the
                // UI canvases at the HUD camera and restores the mask. It is NOT
                // paired with EnsureGameMapSpawned here - spawning the inventory map
                // out of band is what put a black bar across the screen last time.
                int maskBefore = cameras.hudCamera.cullingMask;
                if (maskBefore == 0 || UIManager.instance == null ||
                    UIManager.instance.UICanvas == null ||
                    UIManager.instance.UICanvas.worldCamera != cameras.hudCamera)
                {
                    cameras.MoveMenuToHUDCamera();
                    Plugin.Log.LogInfo(
                        $"Custom Gauntlet: HUD camera was drawing nothing (culling mask {maskBefore}); " +
                        $"repointed the UI canvases at it (mask now {cameras.hudCamera.cullingMask}).");
                }

                // Sent unconditionally rather than checking IsHudVisible first: that
                // flag reads an FSM variable which can say "visible" while the canvas
                // is still parked off-screen, and re-sending IN is harmless.
                cameras.HUDIn();

                if (verbose)
                {
                    Plugin.Log.LogInfo(
                        $"Custom Gauntlet: HUD - camera active={cameras.hudCamera.gameObject.activeSelf}, " +
                        $"enabled={cameras.hudCamera.enabled}, " +
                        $"gameplayChild={(hud != null && hud.GameplayChild != null ? hud.GameplayChild.activeSelf.ToString() : "n/a")}, " +
                        $"slideFsm={(cameras.hudCanvasSlideOut != null ? "present" : "MISSING")}, " +
                        $"cullingMask={cameras.hudCamera.cullingMask}, " +
                        $"reportsVisible={cameras.IsHudVisible}, " +
                        $"gameState={(GameManager.instance != null ? GameManager.instance.GameState.ToString() : "?")}.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: couldn't show the HUD: " + e.Message);
            }
        }


        /// <summary>
        /// Where an imported bench goes: at the home point, nudged aside so you don't
        /// spawn standing inside it.
        /// </summary>
        public static Vector3 BenchPlacementPoint()
        {
            return SnapToGround(HomePoint() + new Vector3(2.5f, 0f, 0f));
        }

        private static Vector3 SnapToGround(Vector3 from)
        {
            return SnapToGround(from, out _);
        }

        /// <summary>Ground-snap for other classes, with an explicit "did it find floor".</summary>
        public static Vector3 GroundAt(Vector3 from, out bool foundFloor)
        {
            return SnapToGround(from, out foundFloor);
        }

        /// <summary>True if a point is inside the room's tilemap bounds.</summary>
        /// <summary>
        /// Is this point somewhere a player could plausibly be standing?
        ///
        /// The margin is the whole point. A bare "x >= 0 && y >= 0" test calls (0, 0)
        /// in-bounds - and (0, 0) is exactly where an unset hazard respawn puts you, in
        /// the bottom-left corner of the room, inside the geometry. So the test that
        /// was meant to catch bad respawns was passing the most common bad respawn
        /// there is. Room edges are never valid standing room, so they're excluded.
        /// </summary>
        public static bool IsInRoomBounds(Vector3 point)
        {
            const float margin = 3f;

            return point.x >= margin && point.x <= RoomWidth() - margin
                   && point.y >= margin && point.y <= RoomHeight() - margin;
        }

        /// <summary>
        /// Drops a point onto the floor beneath it. "Terrain" is the layer name the
        /// game itself uses for solid ground.
        /// </summary>
        private static Vector3 SnapToGround(Vector3 from, out bool foundFloor)
        {
            foundFloor = false;

            try
            {
                int terrain = LayerMask.GetMask("Terrain");
                if (terrain == 0) return from;

                RaycastHit2D hit = Physics2D.Raycast(from + Vector3.up, Vector2.down, 200f, terrain);
                if (hit.collider != null)
                {
                    foundFloor = true;
                    return new Vector3(from.x, hit.point.y, from.z);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: ground snap failed, using the raw point: " + e.Message);
            }

            return from;
        }

        // ------------------------------------------------------------------
        // Reporting
        // ------------------------------------------------------------------

        public static void ReportContents()
        {
            Scene scene = SceneManager.GetActiveScene();
            int benches = UnityEngine.Object.FindObjectsByType<RestBench>(FindObjectsSortMode.None).Length;
            int markers = RespawnMarker.Markers?.Count ?? 0;
            int gates = TransitionPoint.TransitionPoints?.Count ?? 0;

            Vector3 home = HomePoint();
            Bounds? arena = ArenaBounds();

            Plugin.Log.LogInfo(
                $"Custom Gauntlet: '{scene.name}' is {RoomWidth():0.#} wide; {benches} bench(es), " +
                $"{markers} respawn marker(s), {gates} gate(s); enemy library holds {EnemyLibrary.Count}.");
            Plugin.Log.LogInfo(
                $"Custom Gauntlet: arena {(arena.HasValue ? $"spans x {arena.Value.min.x:0.#}..{arena.Value.max.x:0.#}" : "bounds unknown")}, " +
                $"home point at ({home.x:0.#}, {home.y:0.#}){(GauntletConfig.HomePointSet.Value ? " [pinned]" : " [auto]")}.");
        }

        /// <summary>
        /// Deletes any enemy in the room that isn't one of ours, and reports how many.
        ///
        /// This is the backstop for a fight we can't otherwise intercept. Clearing the
        /// room once only helps if the stock enemies are sitting there to be cleared -
        /// if the room's own PlayMaker FSMs instantiate them when you cross the
        /// trigger, they arrive after every setup pass we do. Running this on a timer
        /// catches them however they turn up.
        ///
        /// Our own wave enemies live under GauntletMod_Wave_* objects, so they're
        /// never touched.
        /// </summary>
        public static int SweepForeignEnemies()
        {
            int removed = 0;
            try
            {
                foreach (HealthManager hm in UnityEngine.Object.FindObjectsByType<HealthManager>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (hm == null) continue;
                    if (!IsRoomObject(hm.gameObject)) continue;
                    if (WaveBuilder.IsOurs(hm.gameObject)) continue;

                    HeroController hero = HeroController.instance;
                    if (hero != null && hm.transform.IsChildOf(hero.transform)) continue;

                    UnityEngine.Object.Destroy(hm.gameObject);
                    removed++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: enemy sweep failed: " + e);
            }

            return removed;
        }

        /// <summary>
        /// Writes everything the room actually contains to the log.
        ///
        /// Reading the shipped scene bundles only gets you so far - they're
        /// compressed, so a string that doesn't turn up might genuinely be absent or
        /// might just be in a block `strings` can't see. This is ground truth: what
        /// the running game has loaded, right now.
        /// </summary>
        public static void DumpRoom()
        {
            try
            {
                Plugin.Log.LogInfo("=== Custom Gauntlet room dump ===");
                Plugin.Log.LogInfo("Scenes loaded: " + DescribeLoadedScenes());
                Plugin.Log.LogInfo("Doors: " + DescribeDoorTargets());

                BattleScene[] battles = UnityEngine.Object.FindObjectsByType<BattleScene>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                Plugin.Log.LogInfo($"BattleScenes: {battles.Length}");
                foreach (BattleScene battle in battles)
                {
                    if (battle == null) continue;

                    int waveCount = battle.waves?.Count ?? 0;
                    Plugin.Log.LogInfo(
                        $"  '{battle.name}' in '{battle.gameObject.scene.name}' " +
                        $"active={battle.gameObject.activeInHierarchy} waves={waveCount} " +
                        $"currentWave={battle.currentWave} ours={(WaveBuilder.CurrentBattle == battle)}");

                    if (battle.waves == null) continue;
                    for (int i = 0; i < battle.waves.Count; i++)
                    {
                        BattleWave wave = battle.waves[i];
                        if (wave == null) { Plugin.Log.LogInfo($"    wave {i}: <null>"); continue; }

                        var kids = new List<string>();
                        foreach (Transform child in wave.transform) kids.Add(child.name);

                        Plugin.Log.LogInfo(
                            $"    wave {i}: '{wave.name}' children={kids.Count}" +
                            (kids.Count > 0 ? " [" + string.Join(", ", kids.ToArray()) + "]" : ""));
                    }
                }

                // The important number: enemies that exist but aren't parented under a
                // wave. Those are the ones a wave-based clear never touches.
                var loose = new List<string>();
                int total = 0;
                foreach (HealthManager hm in UnityEngine.Object.FindObjectsByType<HealthManager>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (hm == null || !IsRoomObject(hm.gameObject)) continue;
                    total++;

                    bool underWave = hm.GetComponentInParent<BattleWave>() != null;
                    if (!underWave && loose.Count < 40)
                    {
                        loose.Add($"{hm.name}(active={hm.gameObject.activeInHierarchy})");
                    }
                }

                Plugin.Log.LogInfo($"HealthManagers in room: {total}; not under any BattleWave: {loose.Count}");
                if (loose.Count > 0) Plugin.Log.LogInfo("  loose: " + string.Join(", ", loose.ToArray()));

                Plugin.Log.LogInfo($"Enemy library: {EnemyLibrary.Count} type(s).");
                Plugin.Log.LogInfo("=== end dump ===");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Custom Gauntlet: room dump failed: " + e);
            }
        }

        /// <summary>Gate names and where they lead, for the dev menu.</summary>
        public static string DescribeGates()
        {
            TransitionPoint[] points = UnityEngine.Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None);
            if (points.Length == 0) return "No gates in this scene.";

            return string.Join(", ", points
                .Where(p => p != null)
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.name}->{p.targetScene}")
                .ToArray());
        }

        /// <summary>Respawn markers and benches in the current scene, for the dev menu.</summary>
        public static string DescribeSpawns()
        {
            var names = new List<string>();

            if (RespawnMarker.Markers != null)
            {
                names.AddRange(RespawnMarker.Markers.Where(m => m != null).Select(m => "marker:" + m.name));
            }
            names.AddRange(UnityEngine.Object
                .FindObjectsByType<RestBench>(FindObjectsSortMode.None)
                .Select(b => "bench:" + b.name));

            if (names.Count == 0) return "No respawn markers or benches in this scene.";
            return string.Join(", ", names.OrderBy(n => n).ToArray());
        }
    }
}
