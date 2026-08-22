using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// Turns the saved wave list into an actual fight.
    ///
    /// The key decision here is that we do NOT write a wave system. Silksong's
    /// BattleScene already runs waves properly - it locks the camera, closes the
    /// gates, handles the music cues, counts enemies down via HealthManager death
    /// callbacks, advances to the next wave and opens the gates at the end. A
    /// BattleWave is just a GameObject whose children are the enemies.
    ///
    /// So the gauntlet keeps the room's own BattleScene and only swaps out its
    /// `waves` list. Everything that makes the fight feel like a real Silksong
    /// arena comes along for free, and the stock High Halls enemies get harvested
    /// into the library on the way out rather than simply deleted.
    /// </summary>
    internal static class WaveBuilder
    {
        private const string WaveObjectPrefix = "GauntletMod_Wave_";

        private static readonly List<Vector3> SpawnPoints = new List<Vector3>();

        /// <summary>The battle our waves are built into.</summary>
        public static BattleScene CurrentBattle { get; private set; }

        /// <summary>
        /// Every other BattleScene in the room. The Grand Forum has three, and leaving
        /// even one of them armed means the stock fight still runs alongside yours -
        /// which is exactly what "walking in gives me the normal gauntlet" was.
        /// </summary>
        private static readonly List<BattleScene> OtherBattles = new List<BattleScene>();

        public static int AdoptedCount => (CurrentBattle != null ? 1 : 0) + OtherBattles.Count;

        /// <summary>Enemy ids in the saved waves that the library couldn't supply.</summary>
        public static List<string> LastSkippedEnemies { get; private set; } = new List<string>();

        /// <summary>
        /// Everything the saved waves ask for that isn't loaded yet, by the label you
        /// picked it under. Recomputed on demand rather than cached, because importing
        /// a room can resolve several at once.
        /// </summary>
        public static List<string> MissingEnemies()
        {
            var missing = new List<string>();

            foreach (Wave wave in WaveConfig.Waves)
            {
                foreach (WaveSlot slot in wave.Slots)
                {
                    if (string.IsNullOrEmpty(slot.EnemyId)) continue;
                    if (EnemyLibrary.Resolve(slot.EnemyId) != null) continue;
                    if (!missing.Contains(slot.Label)) missing.Add(slot.Label);
                }
            }

            return missing;
        }

        public static bool HasMissingEnemies => MissingEnemies().Count > 0;

        private static int CountSpawned(List<BattleWave> waves)
        {
            int total = 0;
            foreach (BattleWave wave in waves)
            {
                if (wave != null) total += wave.transform.childCount;
            }
            return total;
        }

        /// <summary>
        /// Takes over every battle in the room: banks their enemies, empties their
        /// waves, and disarms all but the one we'll build into.
        /// </summary>
        public static BattleScene AdoptRoomBattle()
        {
            Scene scene = SceneManager.GetActiveScene();
            OtherBattles.Clear();
            SpawnPoints.Clear();

            var found = new List<BattleScene>();
            foreach (BattleScene candidate in UnityEngine.Object.FindObjectsByType<BattleScene>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate == null || !ArenaSetup.IsRoomObject(candidate.gameObject)) continue;
                found.Add(candidate);
            }

            if (found.Count == 0)
            {
                CurrentBattle = null;
                Plugin.Log.LogWarning(
                    $"WaveBuilder: '{scene.name}' has no BattleScene, so it can't run waves. If this " +
                    "isn't the gauntlet room, stand in the right one and use " +
                    "F9 -> 'Use this room as the gauntlet'.");
                return null;
            }

            BattleScene primary = PickPrimary(found);

            // Set this before building spawn points - working out the arena's extent
            // needs to know which battle we're using.
            CurrentBattle = primary;

            // The room may have been authored to keep its gates shut after the stock
            // fight; we always want out afterwards.
            primary.openGatesOnEnd = true;

            var stockPositions = new List<Vector3>();
            int stockWaves = 0;

            foreach (BattleScene battle in found)
            {
                stockWaves += CountWaves(battle);

                // Harvest before clearing, so the room's own roster ends up in your
                // picker rather than simply being deleted.
                EnemyLibrary.HarvestFromBattleScene(battle);
                CollectSpawnPoints(battle, stockPositions);
                ClearStockWaves(battle);

                if (battle != primary)
                {
                    OtherBattles.Add(battle);
                    SetArmed(battle, false);
                }
            }

            BuildSpawnPoints(primary, stockPositions);

            Plugin.Log.LogInfo(
                $"WaveBuilder: took over {found.Count} BattleScene(s) in '{scene.name}' - " +
                $"{stockWaves} stock wave(s) emptied, {SpawnPoints.Count} spawn point(s) kept. " +
                $"Building into '{primary.name}'" +
                (OtherBattles.Count > 0 ? $"; {OtherBattles.Count} other(s) disarmed." : "."));

            return primary;
        }

        private static int CountWaves(BattleScene battle)
        {
            return battle.waves?.Count ?? 0;
        }

        /// <summary>
        /// Chooses which battle to build into.
        ///
        /// Being *active* matters far more than having the most waves. A room can hold
        /// several BattleScenes for different story states - Hang_04 has an inactive
        /// "Battle Scene Act3" sitting alongside the live one in Hang_04_boss - and
        /// building into a switched-off object silently breaks everything: StartBattle
        /// calls StartCoroutine, Unity refuses to run coroutines on inactive objects,
        /// so the fight never starts and the enemies never turn on. They just sit
        /// there, correctly positioned and permanently invisible.
        /// </summary>
        private static BattleScene PickPrimary(List<BattleScene> found)
        {
            BattleScene best = null;
            int bestScore = int.MinValue;

            foreach (BattleScene candidate in found)
            {
                int score = (candidate.gameObject.activeInHierarchy ? 100000 : 0) + CountWaves(candidate);
                if (score <= bestScore) continue;
                bestScore = score;
                best = candidate;
            }

            if (best != null && !best.gameObject.activeInHierarchy)
            {
                // Nothing active to choose from - switch the best candidate on rather
                // than build into something that can't run.
                Plugin.Log.LogWarning(
                    $"WaveBuilder: '{best.name}' is inactive and it's the only battle here; activating it.");
                ActivateWithAncestors(best.gameObject);
            }

            return best;
        }

        private static void ActivateWithAncestors(GameObject go)
        {
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// Instance ids of everything we've spawned.
        ///
        /// Parentage alone isn't a safe test: some enemies detach themselves from
        /// their parent when they activate, and the moment one does, a
        /// "delete anything that isn't ours" sweep would eat it half a second later.
        /// An id we recorded at spawn time can't be undone by the enemy.
        /// </summary>
        private static readonly HashSet<int> Spawned = new HashSet<int>();

        /// <summary>True if this object is one of our spawned wave enemies.</summary>
        public static bool IsOurs(GameObject go)
        {
            if (go == null) return false;

            if (Spawned.Contains(go.GetInstanceID())) return true;

            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (Spawned.Contains(t.gameObject.GetInstanceID())) return true;
                if (t.name.StartsWith(WaveObjectPrefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// The stock enemies are already standing in sensible places in the arena, so
        /// their positions make far better spawn points than anything we could
        /// compute from the room's bounds.
        /// </summary>
        private static void CollectSpawnPoints(BattleScene battle, List<Vector3> into)
        {
            if (battle.waves == null) return;

            foreach (BattleWave wave in battle.waves)
            {
                if (wave == null) continue;
                foreach (Transform child in wave.transform)
                {
                    if (child != null) into.Add(child.position);
                }
            }
        }

        /// <summary>
        /// Works out where enemies should actually appear.
        ///
        /// Copying the stock enemies' positions sounds ideal and mostly isn't: plenty
        /// of Silksong arena enemies are parked off-screen or above the ceiling so they
        /// can fly or drop in, so reusing those spots verbatim spawns your waves
        /// somewhere you'll never see them. Every candidate is therefore checked -
        /// inside the room, with actual floor beneath it - before it's kept.
        ///
        /// The reliable source is the arena's own extent: points spread across the
        /// fighting area and dropped onto the ground. Validated stock positions are
        /// added on top for variety.
        /// </summary>
        private static void BuildSpawnPoints(BattleScene battle, List<Vector3> stockPositions)
        {
            SpawnPoints.Clear();

            Bounds area = ArenaSetup.ArenaArea();
            int kept = 0, rejected = 0;

            if (GauntletConfig.SpawnAtArenaCentre.Value)
            {
                // Simplest thing that's always visible: everything lands in the middle
                // of the arena, on the floor, fanned out a little so they don't
                // overlap. Spreading enemies across the room's own layout is prettier
                // but puts some of them out of sight, which is worse.
                // Raycast down from just above the *floor you're standing on*, not
                // from the top of the arena. Casting from the ceiling finds the first
                // thing beneath it, which in Hang_04 is a high platform some twenty
                // units above the ground - enemies spawned there are off-screen and
                // look like they never spawned at all.
                float centreX = area.center.x;
                float reference = ArenaSetup.HomePoint().y;

                Vector3 floorAt = ArenaSetup.GroundAt(new Vector3(centreX, reference + 3f, 0f), out bool floor);
                float y = floor ? floorAt.y + GauntletConfig.SpawnHeight.Value : reference;

                // A floor wildly away from where you stand means the cast found
                // something unhelpful; fall back to your own height.
                if (floor && Mathf.Abs(y - reference) > 12f)
                {
                    Plugin.Log.LogWarning(
                        $"WaveBuilder: floor at the arena centre came out as y {y:0.#}, but you're at " +
                        $"{reference:0.#}. Using your height instead.");
                    y = reference;
                }

                for (int i = 0; i < 7; i++)
                {
                    float offset = ((i % 2 == 0) ? 1f : -1f) * 1.8f * ((i + 1) / 2);
                    SpawnPoints.Add(new Vector3(centreX + offset, y, 0f));
                }

                Plugin.Log.LogInfo(
                    $"WaveBuilder: spawning at the arena centre ({centreX:0.#}, {y:0.#})" +
                    (floor ? "." : " - no floor found there, using the trigger's middle height."));
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    float t = 0.2f + 0.15f * i;
                    float x = Mathf.Lerp(area.min.x, area.max.x, t);

                    Vector3 candidate = ArenaSetup.GroundAt(new Vector3(x, area.max.y, 0f), out bool floor);
                    if (floor && ArenaSetup.IsInRoomBounds(candidate))
                    {
                        SpawnPoints.Add(candidate + new Vector3(0f, GauntletConfig.SpawnHeight.Value, 0f));
                    }
                }

                foreach (Vector3 stock in stockPositions)
                {
                    if (!ArenaSetup.IsInRoomBounds(stock)) { rejected++; continue; }

                    ArenaSetup.GroundAt(stock, out bool floor);
                    if (!floor) { rejected++; continue; }

                    SpawnPoints.Add(stock);
                    kept++;
                }
            }

            if (SpawnPoints.Count == 0)
            {
                // Last resort: put them where the player is. Ugly, but visible - which
                // beats an empty-looking arena every time.
                HeroController hero = HeroController.instance;
                Vector3 origin = hero != null ? hero.transform.position : area.center;

                for (int i = 0; i < 3; i++) SpawnPoints.Add(origin + new Vector3(3f + i * 2f, 0f, 0f));

                Plugin.Log.LogWarning(
                    "WaveBuilder: couldn't find any solid ground in the arena, so enemies will spawn " +
                    "next to you. Check the room's floor, or pin a home point with F9.");
            }

            Plugin.Log.LogInfo(
                $"WaveBuilder: {SpawnPoints.Count} spawn point(s) " +
                $"({kept} reused from the room, {rejected} rejected as off-map or in mid-air). " +
                $"First at ({SpawnPoints[0].x:0.#}, {SpawnPoints[0].y:0.#}).");
        }

        private static void ClearStockWaves(BattleScene battle)
        {
            if (battle.waves == null) return;

            int removed = 0;
            foreach (BattleWave wave in battle.waves)
            {
                if (wave == null) continue;

                var doomed = new List<GameObject>();
                foreach (Transform child in wave.transform) doomed.Add(child.gameObject);

                foreach (GameObject go in doomed)
                {
                    UnityEngine.Object.Destroy(go);
                    removed++;
                }
            }

            Plugin.Log.LogInfo($"WaveBuilder: removed {removed} stock enemy placement(s) from the room's waves.");
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the adopted BattleScene's wave list from the saved config. Safe
        /// to call repeatedly - previously built waves are torn down first.
        /// </summary>
        public static bool Rebuild()
        {
            // Re-adopt if what we picked has gone away or been switched off - the room
            // can load a second battle additively after our first pass.
            if (CurrentBattle == null || !CurrentBattle.gameObject.activeInHierarchy)
            {
                AdoptRoomBattle();
            }

            BattleScene battle = CurrentBattle;
            if (battle == null)
            {
                Plugin.Log.LogWarning("WaveBuilder: nothing to rebuild into - no BattleScene adopted.");
                return false;
            }

            try
            {
                DestroyBuiltWaves(battle);
                Spawned.Clear();

                // Clear the room again, dormant enemies included. The stock fight can
                // be re-armed by the room's own FSMs between us taking over and you
                // pressing Apply, and our own waves are already gone by this point so
                // there's nothing of ours to lose.
                ArenaSetup.StripRoom(includeInactive: true);

                var built = new List<BattleWave>();
                int spawnCursor = 0;
                int skipped = 0;
                var missing = new List<string>();

                for (int i = 0; i < WaveConfig.Waves.Count; i++)
                {
                    Wave source = WaveConfig.Waves[i];
                    if (source.TotalEnemies == 0) continue;

                    var holder = new GameObject(WaveObjectPrefix + (i + 1));
                    holder.transform.SetParent(battle.transform, false);

                    BattleWave wave = holder.AddComponent<BattleWave>();
                    wave.startDelay = source.StartDelay + (built.Count == 0 ? GauntletConfig.FirstWaveDelay.Value : 0f);
                    wave.remainingEnemyToEnd = 0;
                    wave.activateEnemiesOnStart = true;

                    foreach (WaveSlot slot in source.Slots)
                    {
                        EnemyEntry entry = EnemyLibrary.Resolve(slot.EnemyId);
                        if (entry == null)
                        {
                            skipped += Math.Max(1, slot.Count);
                            if (!missing.Contains(slot.EnemyId)) missing.Add(slot.EnemyId);
                            continue;
                        }

                        for (int n = 0; n < Math.Max(1, slot.Count); n++)
                        {
                            Vector3 at = SpawnPoints.Count > 0
                                ? SpawnPoints[spawnCursor % SpawnPoints.Count]
                                : battle.transform.position;

                            // A room might only donate two spawn points while you've
                            // asked for six enemies, so fan them out rather than
                            // stacking them all in one spot.
                            int lap = spawnCursor / Mathf.Max(1, SpawnPoints.Count);
                            if (lap > 0)
                            {
                                float offset = ((lap % 2 == 0) ? 1f : -1f) * 1.6f * ((lap + 1) / 2);
                                at += new Vector3(offset, 0f, 0f);
                            }

                            spawnCursor++;

                            GameObject spawned = EnemyLibrary.Spawn(entry, holder.transform, at);
                            if (spawned != null) Spawned.Add(spawned.GetInstanceID());
                        }
                    }

                    // A wave with nothing in it would stall the fight, since the
                    // BattleScene waits for an enemy count that never arrives.
                    if (holder.transform.childCount == 0)
                    {
                        UnityEngine.Object.Destroy(holder);
                        continue;
                    }

                    wave.Init(battle);
                    built.Add(wave);
                }

                battle.waves = built;
                ResetBattleState(battle);

                // Held shut until every enemy in the waves exists. Starting a run that
                // silently drops half its roster is worse than not starting it.
                bool complete = built.Count > 0 && missing.Count == 0;
                SetArmed(battle, complete);
                DisarmOthers();

                // Our own trigger, so walking in starts your waves regardless of what
                // survived of the room's original wiring.
                if (built.Count > 0) ArenaTrigger.Ensure(battle, ArenaSetup.ArenaArea());
                else ArenaTrigger.SetEnabled(battle, false);

                LastSkippedEnemies = missing;

                if (skipped > 0)
                {
                    Plugin.Log.LogWarning(
                        $"WaveBuilder: skipped {skipped} enemy placement(s) not in the library: " +
                        string.Join(", ", missing.ToArray()) +
                        ". Enemies have to be met in a room before they can be spawned.");
                }

                Plugin.Log.LogInfo(
                    $"WaveBuilder: built {built.Count} wave(s), " +
                    $"{CountSpawned(built)} enemy/enemies placed.");

                return built.Count > 0;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("WaveBuilder: rebuild failed: " + e);
                return false;
            }
        }

        private static void DestroyBuiltWaves(BattleScene battle)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in battle.transform)
            {
                if (child != null && child.name.StartsWith(WaveObjectPrefix, StringComparison.Ordinal))
                {
                    doomed.Add(child.gameObject);
                }
            }

            foreach (GameObject go in doomed) UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// Puts the battle back to "not started" so it can be triggered again. Most
        /// of this state is private, which is fine - it's read-once bookkeeping, not
        /// something with a public reset.
        /// </summary>
        public static void ResetBattleState(BattleScene battle)
        {
            if (battle == null) return;

            battle.currentWave = 0;
            battle.currentEnemies = 0;
            battle.enemiesToNext = 0;

            SetPrivate(battle, "started", false);
            SetPrivate(battle, "completed", false);
            SetPrivate(battle, "loopsUntilDeactivate", 0);

            // DoStartBattle disables these so you can't re-trigger mid-fight.
            foreach (Collider2D col in battle.GetComponents<Collider2D>())
            {
                if (col.isTrigger) col.enabled = true;
            }
        }

        /// <summary>
        /// Turns the arena's entry trigger on or off.
        ///
        /// With no waves configured the room still has its five now-empty stock wave
        /// objects, so walking in would start a fight that instantly completes -
        /// gates slamming, music cue, nothing to hit. Better to leave the trigger off
        /// until there's actually something to fight.
        /// </summary>
        public static void SetArmed(BattleScene battle, bool armed)
        {
            if (battle == null) return;

            foreach (Collider2D col in battle.GetComponents<Collider2D>())
            {
                if (col.isTrigger) col.enabled = armed;
            }

            if (!armed)
            {
                Plugin.Log.LogInfo("WaveBuilder: the arena trigger is off - either no waves, or enemies still to load.");
            }
        }

        /// <summary>Leaves every adopted arena inert - used when there's nothing to run.</summary>
        public static void Disarm()
        {
            SetArmed(CurrentBattle, false);
            foreach (BattleScene other in OtherBattles) SetArmed(other, false);
        }

        /// <summary>Keeps the secondary battles switched off after any rebuild.</summary>
        private static void DisarmOthers()
        {
            foreach (BattleScene other in OtherBattles) SetArmed(other, false);
        }

        /// <summary>True while a fight we started is still in progress.</summary>
        public static bool IsBattleRunning
        {
            get
            {
                BattleScene battle = CurrentBattle;
                if (battle == null) return false;

                object started = GetPrivate(battle, "started");
                return started is bool flag && flag;
            }
        }

        private static object GetPrivate(object target, string field)
        {
            try
            {
                FieldInfo info = AccessTools.Field(target.GetType(), field);
                return info?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Starts the fight immediately, without waiting for the trigger.</summary>
        public static bool StartBattleNow()
        {
            BattleScene battle = CurrentBattle;
            if (battle == null) return false;

            if (HasMissingEnemies)
            {
                Plugin.Log.LogWarning(
                    "WaveBuilder: not starting - these still need loading: " +
                    string.Join(", ", MissingEnemies().ToArray()));
                return false;
            }

            ResetBattleState(battle);
            GauntletRun.Begin(battle.waves?.Count ?? 0);
            battle.StartBattle();
            ReportPlacements();
            return true;
        }

        private static readonly Dictionary<int, int> BuriedFor = new Dictionary<int, int>();

        /// <summary>
        /// Lifts wave enemies that have ended up under the floor.
        ///
        /// Burrowing enemies - the choir clappers especially - play an emerge
        /// animation from wherever they were placed, and in a room they weren't
        /// authored for that can leave them below the ground with nothing to emerge
        /// from. Spawning them higher didn't help, because it's the animation that
        /// takes them down, not the spawn position.
        ///
        /// So this watches instead of predicting: an enemy that's been below the floor
        /// for several seconds running isn't mid-animation, it's stuck. Anything
        /// briefly underground - which is a legitimate attack for some of them - is
        /// left alone.
        /// </summary>
        public static int UnstickBuriedEnemies()
        {
            BattleScene battle = CurrentBattle;
            if (battle?.waves == null) return 0;

            int lifted = 0;

            foreach (BattleWave wave in battle.waves)
            {
                if (wave == null) continue;

                foreach (Transform child in wave.transform)
                {
                    if (child == null || !child.gameObject.activeInHierarchy) continue;

                    int id = child.gameObject.GetInstanceID();
                    Vector3 at = child.position;

                    Vector3 ground = ArenaSetup.GroundAt(at + new Vector3(0f, 6f, 0f), out bool foundFloor);
                    bool buried = foundFloor && at.y < ground.y - 0.75f;

                    if (!buried)
                    {
                        BuriedFor.Remove(id);
                        continue;
                    }

                    BuriedFor.TryGetValue(id, out int ticks);
                    ticks++;
                    BuriedFor[id] = ticks;

                    if (ticks < 4) continue; // ~2s at the watchdog's cadence

                    child.position = new Vector3(at.x, ground.y + GauntletConfig.SpawnHeight.Value, at.z);
                    BuriedFor.Remove(id);
                    lifted++;

                    Plugin.Log.LogInfo($"WaveBuilder: '{child.name}' was stuck under the floor - lifted it out.");
                }
            }

            return lifted;
        }

        /// <summary>Position of the first enemy we spawned, if any are still alive.</summary>
        public static Vector3? FirstSpawnedPosition()
        {
            BattleScene battle = CurrentBattle;
            if (battle?.waves == null) return null;

            foreach (BattleWave wave in battle.waves)
            {
                if (wave == null) continue;
                foreach (Transform child in wave.transform)
                {
                    if (child != null) return child.position;
                }
            }
            return null;
        }

        /// <summary>
        /// Logs where every spawned enemy actually is, and whether the hero can see
        /// it. "The fight started but there's nothing there" has several possible
        /// causes - wrong position, off-map, never activated - and they're
        /// indistinguishable in-game but obvious from this.
        /// </summary>
        public static void ReportPlacements()
        {
            BattleScene battle = CurrentBattle;
            if (battle == null || battle.waves == null) return;

            HeroController hero = HeroController.instance;
            Vector3 heroAt = hero != null ? hero.transform.position : Vector3.zero;

            for (int w = 0; w < battle.waves.Count; w++)
            {
                BattleWave wave = battle.waves[w];
                if (wave == null) continue;

                foreach (Transform child in wave.transform)
                {
                    if (child == null) continue;

                    float distance = Vector3.Distance(child.position, heroAt);
                    Plugin.Log.LogInfo(
                        $"WaveBuilder: wave {w + 1} '{child.name}' at " +
                        $"({child.position.x:0.#}, {child.position.y:0.#}) " +
                        $"active={child.gameObject.activeInHierarchy} " +
                        $"inRoom={ArenaSetup.IsInRoomBounds(child.position)} " +
                        $"{distance:0.#} from you.");
                }
            }
        }

        private static void SetPrivate(object target, string field, object value)
        {
            try
            {
                FieldInfo info = AccessTools.Field(target.GetType(), field);
                info?.SetValue(target, value);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"WaveBuilder: couldn't reset BattleScene.{field}: {e.Message}");
            }
        }
    }
}
