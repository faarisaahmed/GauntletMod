using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// Puts a real, working bench in a room that doesn't have one.
    ///
    /// A bench can't be fabricated from scratch: RestBench itself only flips
    /// HeroController's "near a bench" flag, and everything you actually care about -
    /// sitting, healing, saving, setting the respawn point - lives in the bench's
    /// "Bench Control" PlayMaker FSM. So the bench has to be a real one lifted out of
    /// a real scene.
    ///
    /// The donor scene is loaded additively once, the bench is cloned into an
    /// inactive holder, and the donor is then left loaded but fully deactivated. It
    /// stays loaded on purpose: unloading it would release its asset bundle, and the
    /// cloned bench's sprites and animations live in that bundle. The default donor
    /// is about a megabyte, which is a cheap price for a bench that behaves.
    /// </summary>
    internal static class BenchImporter
    {
        private static GameObject _template;
        private static GameObject _holder;
        private static bool _importFailed;

        /// <summary>
        /// Tags GameManager searches for by name. A second object carrying one of
        /// these in an additively-loaded scene can hijack the room's scene manager or
        /// tilemap lookup, so they get cleared before anything else happens.
        /// </summary>
        private static readonly string[] HijackableTags = { "SceneManager", "TileMap" };

        public static RestBench FindBenchInActiveScene()
        {
            foreach (RestBench bench in UnityEngine.Object.FindObjectsByType<RestBench>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (bench != null && ArenaSetup.IsRoomObject(bench.gameObject) && bench.gameObject.activeInHierarchy)
                {
                    return bench;
                }
            }
            return null;
        }

        private const string PlacedName = "GauntletMod_Bench";

        /// <summary>
        /// Moves the bench we placed to a new spot, importing one first if there isn't
        /// one yet. Used by the dev menu's "add the bench here", so you can nudge it
        /// rather than living with wherever it first landed.
        /// </summary>
        public static IEnumerator PlaceAt(Vector3 position)
        {
            GameObject existing = GameObject.Find(PlacedName);
            if (existing != null)
            {
                existing.transform.position = position;
                MakeVisible(existing);
                SitOnGround(existing, position.y);
                Plugin.Log.LogInfo($"BenchImporter: moved the bench to {position}.");
                yield break;
            }

            yield return EnsureBench(position, force: true);
        }

        /// <summary>
        /// Ensures the current room has a bench, importing one if needed. Does nothing
        /// if the room already has one, unless <paramref name="force"/> is set.
        /// </summary>
        public static IEnumerator EnsureBench(Vector3 position, bool force = false)
        {
            if (!force && FindBenchInActiveScene() != null)
            {
                Plugin.Log.LogInfo("BenchImporter: room already has a bench, nothing to do.");
                yield break;
            }

            if (_template == null && !_importFailed)
            {
                yield return ImportTemplate(GauntletConfig.BenchDonorScene.Value);
            }

            if (_template == null)
            {
                Plugin.Log.LogWarning("BenchImporter: no bench template available; the room will have no bench.");
                yield break;
            }

            try
            {
                GameObject copy = UnityEngine.Object.Instantiate(_template);
                copy.name = PlacedName;
                SceneManager.MoveGameObjectToScene(copy, SceneManager.GetActiveScene());
                copy.transform.position = position;
                copy.SetActive(true);

                MakeVisible(copy);
                SitOnGround(copy, position.y);

                Plugin.Log.LogInfo($"BenchImporter: placed a bench at {position}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("BenchImporter: failed to place the bench: " + e);
            }
        }

        // ------------------------------------------------------------------

        private static IEnumerator ImportTemplate(string donorScene)
        {
            if (string.IsNullOrEmpty(donorScene))
            {
                _importFailed = true;
                yield break;
            }

            Plugin.Log.LogInfo($"BenchImporter: loading '{donorScene}' to lift a bench out of it.");

            AsyncOperationHandle<SceneInstance> handle;
            try
            {
                handle = Addressables.LoadSceneAsync("Scenes/" + donorScene, LoadSceneMode.Additive, activateOnLoad: true);
            }
            catch (Exception e)
            {
                _importFailed = true;
                Plugin.Log.LogError($"BenchImporter: couldn't start loading '{donorScene}': {e}");
                yield break;
            }

            yield return handle;

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                _importFailed = true;
                Plugin.Log.LogError($"BenchImporter: '{donorScene}' failed to load. Check the scene name.");
                yield break;
            }

            Scene donor = handle.Result.Scene;

            try
            {
                // Do this first: until the tags are cleared, the donor's own scene
                // manager is a candidate for GameObject.FindGameObjectWithTag.
                ScrubHijackableTags(donor);

                GameObject benchRoot = FindBenchRoot(donor);
                if (benchRoot == null)
                {
                    _importFailed = true;
                    Plugin.Log.LogError($"BenchImporter: no RestBench found in '{donorScene}'.");
                }
                else
                {
                    EnsureHolder();
                    _template = UnityEngine.Object.Instantiate(benchRoot, _holder.transform);
                    _template.name = "BenchTemplate";
                    _template.SetActive(false);
                    Plugin.Log.LogInfo($"BenchImporter: cloned bench '{benchRoot.name}' from '{donorScene}'.");
                }

                // Leave the scene loaded (its bundle owns the bench's art) but stop it
                // from doing anything: no scenery drawn, no FSMs ticking, no colliders.
                foreach (GameObject root in donor.GetRootGameObjects())
                {
                    root.SetActive(false);
                }
            }
            catch (Exception e)
            {
                _importFailed = true;
                Plugin.Log.LogError("BenchImporter: extraction failed: " + e);
            }
        }

        /// <summary>
        /// Clears anything that would leave a cloned bench invisible, and reports what
        /// it found.
        ///
        /// Deliberately gentle. An earlier version force-activated every object that
        /// owned a renderer and broadcast the bench "appear" events - which on a
        /// Bellway bench woke its unlock-shrine popup and drew a giant glowing sigil in
        /// mid-air instead of a bench. A plain bench needs none of that; the only thing
        /// worth touching is the fade group, whose alpha a clone can inherit at zero.
        ///
        /// The fade group is reached by reflection because it lives in
        /// TeamCherry.NestedFadeGroup, and taking a reference on that assembly just to
        /// set one float isn't worth it.
        /// </summary>
        private static void MakeVisible(GameObject bench)
        {
            int faded = 0;

            try
            {
                foreach (Component component in bench.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    Type type = component.GetType();
                    if (type.Name.IndexOf("NestedFadeGroup", StringComparison.Ordinal) < 0) continue;

                    foreach (string name in new[] { "AlphaSelf", "Alpha" })
                    {
                        PropertyInfo property = type.GetProperty(name);
                        if (property != null && property.CanWrite && property.PropertyType == typeof(float))
                        {
                            property.SetValue(component, 1f, null);
                            faded++;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("BenchImporter: couldn't clear fade groups: " + e.Message);
            }

            Report(bench, faded);
        }

        private static void Report(GameObject bench, int faded)
        {
            try
            {
                Renderer[] renderers = bench.GetComponentsInChildren<Renderer>(true);
                int visible = 0;

                foreach (Renderer renderer in renderers)
                {
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy) visible++;
                }

                Plugin.Log.LogInfo(
                    $"BenchImporter: {renderers.Length} renderer(s), {visible} drawing, {faded} fade group(s) cleared.");

                if (visible == 0)
                {
                    Plugin.Log.LogWarning(
                        "BenchImporter: the bench is present but nothing is drawing. Try a different " +
                        "Room.BenchDonorScene - and avoid the Bellway rooms, their benches are shrines " +
                        "that only appear once unlocked.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("BenchImporter: couldn't report on the bench: " + e.Message);
            }
        }

        /// <summary>
        /// Drops the bench so its *artwork* rests on the floor.
        ///
        /// The bell bench's pivot isn't at its base - its renderers sit about eight
        /// units below the object's origin - so placing the transform on the ground
        /// buries the whole thing. Measuring the combined renderer bounds and shifting
        /// by the difference puts it where you'd expect regardless of how any
        /// particular donor bench is rigged.
        /// </summary>
        private static void SitOnGround(GameObject bench, float groundY)
        {
            try
            {
                Bounds? combined = null;

                foreach (Renderer renderer in bench.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled) continue;
                    if (renderer.bounds.size == Vector3.zero) continue;

                    // Skip the things that aren't the bench: particle effects, and any
                    // renderer far larger than a bench could be - glows and light
                    // planes hang well below the object and would make it float.
                    if (renderer.GetType().Name == "ParticleSystemRenderer") continue;
                    if (renderer.bounds.size.y > 12f || renderer.bounds.size.x > 20f) continue;

                    if (combined.HasValue)
                    {
                        Bounds grown = combined.Value;
                        grown.Encapsulate(renderer.bounds);
                        combined = grown;
                    }
                    else
                    {
                        combined = renderer.bounds;
                    }
                }

                if (!combined.HasValue)
                {
                    Plugin.Log.LogWarning("BenchImporter: nothing to measure, leaving the bench where it is.");
                    return;
                }

                float lift = groundY - combined.Value.min.y + GauntletConfig.BenchYOffset.Value;
                bench.transform.position += new Vector3(0f, lift, 0f);

                Plugin.Log.LogInfo(
                    $"BenchImporter: art spanned y {combined.Value.min.y:0.#}..{combined.Value.max.y:0.#}, " +
                    $"lifted {lift:0.#} to sit on the floor at y {groundY:0.#}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("BenchImporter: couldn't sit the bench on the ground: " + e.Message);
            }
        }

        private static void Report(GameObject bench, int activated, int enabled, int faded)
        {
            try
            {
                Renderer[] renderers = bench.GetComponentsInChildren<Renderer>(true);
                int visible = 0;
                var describedFirst = "none";

                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null) continue;
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy) visible++;
                }

                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled) continue;
                    describedFirst =
                        $"{renderer.name} layer='{renderer.sortingLayerName}' order={renderer.sortingOrder} " +
                        $"bounds={renderer.bounds.center}";
                    break;
                }

                Plugin.Log.LogInfo(
                    $"BenchImporter: {renderers.Length} renderer(s), {visible} now drawing " +
                    $"(woke {activated} object(s), enabled {enabled}, cleared {faded} fade group(s)). First: {describedFirst}");

                if (visible == 0)
                {
                    Plugin.Log.LogWarning(
                        "BenchImporter: the bench has no drawing renderers - it's present but invisible. " +
                        "Try a different Room.BenchDonorScene (Bellway_Shadow, Bellway_Aqueduct, Bellway_City).");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("BenchImporter: couldn't report on the bench: " + e.Message);
            }
        }

        internal static void ScrubHijackableTags(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    foreach (string tag in HijackableTags)
                    {
                        if (t.CompareTag(tag))
                        {
                            t.gameObject.tag = "Untagged";
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The bench's FSM usually sits on an ancestor of the RestBench trigger, so
        /// take the highest ancestor still recognisably part of the bench rather than
        /// the trigger alone (which would leave the sit behaviour behind) or the
        /// scene root (which would drag half the room along).
        /// </summary>
        private static GameObject FindBenchRoot(Scene scene)
        {
            RestBench bench = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                bench = root.GetComponentInChildren<RestBench>(true);
                if (bench != null) break;
            }

            if (bench == null) return null;

            GameObject best = bench.gameObject;
            Transform cursor = bench.transform.parent;

            while (cursor != null)
            {
                if (cursor.name.IndexOf("bench", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = cursor.gameObject;
                }
                cursor = cursor.parent;
            }

            return best;
        }

        private static void EnsureHolder()
        {
            if (_holder != null) return;

            _holder = new GameObject("GauntletMod_BenchTemplate");
            _holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_holder);
        }
    }
}
