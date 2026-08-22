using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// One spawnable enemy: an inactive template we can clone, plus the Hunter's
    /// Journal name and icon to show for it in the wave editor.
    /// </summary>
    internal class EnemyEntry
    {
        /// <summary>Stable key used in the saved wave file. The template's object name.</summary>
        public string Id;

        /// <summary>Hunter's Journal display name, or the object name if it has no record.</summary>
        public string DisplayName;

        /// <summary>The little circle icon the journal shows for this enemy.</summary>
        public Sprite Icon;

        /// <summary>Inactive clone, parented under a disabled root so its Awake never ran.</summary>
        public GameObject Template;

        public bool HasJournalRecord;

        /// <summary>The journal entry this enemy belongs to, when it has one.</summary>
        public EnemyJournalRecord Record;
    }

    /// <summary>
    /// Where spawnable enemies come from.
    ///
    /// Silksong doesn't expose enemies as addressable prefabs - only six prefabs are
    /// addressable at all, and none of them are enemies. Enemies exist as objects
    /// inside scenes. So the library is built by *harvesting*: whenever we're in a
    /// room that contains enemies, each distinct one is cloned into an inactive,
    /// DontDestroyOnLoad holder and kept as a template we can stamp out copies from
    /// later.
    ///
    /// Cloning into an already-disabled parent matters: Unity skips Awake on objects
    /// instantiated inactive, so the template never runs any enemy logic, never
    /// registers with the object pool, and never shows up in the room.
    ///
    /// The practical consequence is that the picker only offers enemies we've
    /// actually met. The High Halls gauntlet's own five waves seed it on arrival;
    /// walking through other rooms with harvesting on adds to it.
    /// </summary>
    internal static class EnemyLibrary
    {
        /// <summary>
        /// Ceiling on how many enemy types we hold. Each entry is a full enemy
        /// hierarchy kept alive forever, so an unbounded library would leak across a
        /// long session of walking around collecting things.
        /// </summary>
        private const int MaxEntries = 200;

        private static readonly Dictionary<string, EnemyEntry> Entries = new Dictionary<string, EnemyEntry>();
        private static GameObject _root;
        private static FieldInfo _journalField;
        private static bool _warnedFull;

        public static int Count => Entries.Count;

        public static IEnumerable<EnemyEntry> All => Entries.Values.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase);

        public static EnemyEntry Get(string id)
        {
            return id != null && Entries.TryGetValue(id, out EnemyEntry entry) ? entry : null;
        }

        /// <summary>
        /// Finds a banked enemy from whatever a wave slot recorded.
        ///
        /// Slots can be written before the enemy exists - you pick something out of the
        /// Hunter's Journal and its room gets imported later - so the id might be an
        /// object name, a journal record name, or a display name. All three resolve to
        /// the same enemy once it's actually loaded.
        /// </summary>
        public static EnemyEntry Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (Entries.TryGetValue(id, out EnemyEntry direct)) return direct;

            foreach (EnemyEntry entry in Entries.Values)
            {
                if (entry.Record != null &&
                    string.Equals(entry.Record.name, id, StringComparison.OrdinalIgnoreCase)) return entry;

                if (string.Equals(entry.DisplayName, id, StringComparison.OrdinalIgnoreCase)) return entry;
            }

            return null;
        }

        /// <summary>The banked enemy for a journal entry, or null if it isn't loaded.</summary>
        public static EnemyEntry FindByRecord(EnemyJournalRecord record)
        {
            if (record == null) return null;

            foreach (EnemyEntry entry in Entries.Values)
            {
                if (entry.Record == record) return entry;
            }
            return null;
        }

        /// <summary>Banked enemies that have no journal entry - scenery beasts and the like.</summary>
        public static IEnumerable<EnemyEntry> WithoutRecord
        {
            get
            {
                foreach (EnemyEntry entry in Entries.Values)
                {
                    if (entry.Record == null) yield return entry;
                }
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Scans the active scene for enemies and adds any we haven't seen before.
        /// Cheap enough to call on every arena load; existing entries are kept.
        /// </summary>
        public static int HarvestActiveScene()
        {
            int added = 0;
            try
            {

                foreach (HealthManager hm in UnityEngine.Object.FindObjectsByType<HealthManager>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (hm == null) continue;
                    if (!ArenaSetup.IsRoomObject(hm.gameObject)) continue;
                    if (IsHeroOwned(hm.gameObject)) continue;

                    if (TryAdd(hm.gameObject)) added++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("EnemyLibrary: harvest failed: " + e);
            }

            if (added > 0)
            {
                Plugin.Log.LogInfo($"EnemyLibrary: harvested {added} new enemy type(s); library now holds {Entries.Count}.");
            }
            return added;
        }

        /// <summary>
        /// Harvests every enemy a scene can produce.
        ///
        /// Two sources, and the second is the one that matters. Only a minority of
        /// Silksong's enemies are placed in a room as objects; most are *pooled* -
        /// the room carries a PersonalObjectPool listing prefabs, and instances are
        /// created from it at runtime. Looking only for HealthManagers in the loaded
        /// scene therefore finds almost nothing, which is exactly what happened:
        /// every imported room banked zero.
        ///
        /// Reading the pool's prefab references instead needs no instances and no
        /// active objects, so the room can be hidden the moment it loads.
        /// </summary>
        public static int HarvestScene(Scene scene)
        {
            int added = 0;

            try
            {
                // Pooled enemies - the prefabs the room would have spawned.
                foreach (PersonalObjectPool pool in UnityEngine.Object.FindObjectsByType<PersonalObjectPool>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (pool == null || pool.gameObject.scene != scene) continue;
                    if (pool.startupPool == null) continue;

                    foreach (StartupPool entry in pool.startupPool)
                    {
                        GameObject prefab = entry.prefab;
                        if (prefab == null) continue;
                        if (prefab.GetComponentInChildren<HealthManager>(true) == null) continue;

                        if (TryAdd(prefab)) added++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("EnemyLibrary: pool harvest failed: " + ex);
            }

            try
            {
                // Enemies placed directly in the room.
                foreach (HealthManager hm in UnityEngine.Object.FindObjectsByType<HealthManager>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (hm == null) continue;
                    if (hm.gameObject.scene != scene) continue;
                    if (IsHeroOwned(hm.gameObject)) continue;

                    if (TryAdd(hm.gameObject)) added++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("EnemyLibrary: scene harvest failed: " + ex);
            }

            Plugin.Log.LogInfo($"EnemyLibrary: banked {added} enemy type(s) from '{scene.name}'; library holds {Entries.Count}.");
            return added;
        }

        /// <summary>
        /// Harvests specifically out of a BattleScene's stock waves. Called before we
        /// clear the room, so the gauntlet's own roster survives being emptied out.
        /// </summary>
        public static int HarvestFromBattleScene(BattleScene battle)
        {
            int added = 0;
            if (battle == null || battle.waves == null) return 0;

            foreach (BattleWave wave in battle.waves)
            {
                if (wave == null) continue;

                foreach (Transform child in wave.transform)
                {
                    if (child == null) continue;
                    if (child.GetComponent<HealthManager>() == null) continue;

                    // refresh: these come from the room the fight happens in, so
                    // they're the copies whose art is guaranteed to still be loaded.
                    if (TryAdd(child.gameObject, refresh: true)) added++;
                }
            }

            if (added > 0)
            {
                Plugin.Log.LogInfo($"EnemyLibrary: took {added} enemy type(s) from the room's stock waves.");
            }
            return added;
        }

        // ------------------------------------------------------------------

        /// <param name="refresh">
        /// Re-clone even if we already have this enemy. Used when harvesting from the
        /// room the waves will actually run in: a template cloned out of an earlier
        /// room can lose its sprites when that room's asset bundle is released, so
        /// the freshest copy from the live room is always the better one.
        /// </param>
        private static bool TryAdd(GameObject source, bool refresh = false)
        {
            string id = Normalise(source.name);
            if (string.IsNullOrEmpty(id)) return false;

            if (Entries.TryGetValue(id, out EnemyEntry existing))
            {
                // Replace if asked to, or if the old template has been destroyed.
                if (!refresh && existing.Template != null) return false;
                if (existing.Template != null) UnityEngine.Object.Destroy(existing.Template);
                Entries.Remove(id);
            }
            else if (Entries.Count >= MaxEntries)
            {
                if (!_warnedFull)
                {
                    _warnedFull = true;
                    Plugin.Log.LogWarning(
                        $"EnemyLibrary: holding the cap of {MaxEntries} enemy types; new ones are being ignored. " +
                        "Turn Behaviour.HarvestEnemies off, or restart, to reset it.");
                }
                return false;
            }

            EnsureRoot();

            GameObject template;
            try
            {
                // Parent is inactive, so the clone is born inactive and Unity never
                // calls Awake on it. This is what makes a live scene object safe to
                // keep as a template.
                template = UnityEngine.Object.Instantiate(source, _root.transform);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"EnemyLibrary: couldn't clone '{id}': {e.Message}");
                return false;
            }

            template.name = id;

            // Detach from any scene: the room this came from may be unloaded moments
            // from now, and anything still parented into it would be destroyed with it.
            // The holder is DontDestroyOnLoad, so this survives.
            template.transform.SetParent(_root.transform, worldPositionStays: false);

            // Clear activeSelf as well as relying on the inactive parent: copies made
            // from this template are then born inactive even under a live parent, so
            // their Awake waits until the wave actually starts them.
            template.SetActive(false);

            EnemyJournalRecord record = GetJournalRecord(template);

            Entries[id] = new EnemyEntry
            {
                Id = id,
                Template = template,
                HasJournalRecord = record != null,
                Record = record,
                DisplayName = record != null ? SafeDisplayName(record, id) : id,
                Icon = record != null ? record.IconSprite : null,
            };

            return true;
        }

        private static void EnsureRoot()
        {
            if (_root != null) return;

            _root = new GameObject("GauntletMod_EnemyTemplates");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
        }

        /// <summary>
        /// Unity appends "(Clone)"/" (1)" to instantiated objects; strip that so the
        /// same enemy met in two rooms doesn't become two library entries.
        /// </summary>
        private static string Normalise(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            string result = name;
            while (result.EndsWith("(Clone)", StringComparison.Ordinal))
            {
                result = result.Substring(0, result.Length - "(Clone)".Length).TrimEnd();
            }

            int paren = result.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && result.EndsWith(")", StringComparison.Ordinal))
            {
                string inner = result.Substring(paren + 2, result.Length - paren - 3);
                if (inner.Length > 0 && inner.All(char.IsDigit))
                {
                    result = result.Substring(0, paren);
                }
            }

            return result.Trim();
        }

        /// <summary>
        /// The journal record lives on the enemy's EnemyDeathEffects component, in a
        /// private field - it's the only link from a live enemy back to its Hunter's
        /// Journal entry, so name and icon both come through here.
        /// </summary>
        private static EnemyJournalRecord GetJournalRecord(GameObject enemy)
        {
            try
            {
                EnemyDeathEffects effects = enemy.GetComponent<EnemyDeathEffects>()
                                            ?? enemy.GetComponentInChildren<EnemyDeathEffects>(true);
                if (effects == null) return null;

                if (_journalField == null)
                {
                    _journalField = AccessTools.Field(typeof(EnemyDeathEffects), "journalRecord");
                    if (_journalField == null)
                    {
                        Plugin.Log.LogWarning("EnemyLibrary: EnemyDeathEffects.journalRecord not found; falling back to object names.");
                        return null;
                    }
                }

                return _journalField.GetValue(effects) as EnemyJournalRecord;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("EnemyLibrary: journal lookup failed: " + e.Message);
                return null;
            }
        }

        private static string SafeDisplayName(EnemyJournalRecord record, string fallback)
        {
            try
            {
                // LocalisedString has an implicit string conversion that resolves
                // against the current language.
                string name = record.DisplayName;
                return string.IsNullOrWhiteSpace(name) ? fallback : name;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool IsHeroOwned(GameObject go)
        {
            HeroController hero = HeroController.instance;
            return hero != null && go.transform.IsChildOf(hero.transform);
        }

        /// <summary>Stamps out a live copy of a template under the given parent.</summary>
        public static GameObject Spawn(EnemyEntry entry, Transform parent, Vector3 position)
        {
            if (entry?.Template == null) return null;

            GameObject spawned = UnityEngine.Object.Instantiate(entry.Template, parent);
            spawned.name = entry.Id;
            spawned.transform.position = position;
            spawned.SetActive(false); // the wave activates it when its turn comes
            return spawned;
        }
    }
}
