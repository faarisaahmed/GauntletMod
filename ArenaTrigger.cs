using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// Our own "walk in and the fight starts" trigger.
    ///
    /// The room's original trigger can't be relied on. Some of it is a BattleScene
    /// collider we can re-enable, but the Grand Forum also drives its fight from
    /// PlayMaker FSMs, and once those are cleared out along with the stock enemies
    /// there may be nothing left to fire. Rather than reverse-engineer which bit of
    /// the room's wiring survived, the mod places a trigger of its own across the
    /// arena and starts the battle itself.
    ///
    /// That makes stepping in work the same way every time, regardless of how the
    /// original room happened to be built.
    /// </summary>
    internal class ArenaTrigger : MonoBehaviour
    {
        public const string ObjectName = "GauntletMod_ArenaTrigger";

        private float _cooldownUntil;

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other == null) return;

            // Same test the game's own BattleScene uses.
            if (!other.CompareTag("Player"))
            {
                if (other.GetComponent<HeroController>() == null) return;
            }

            if (Time.unscaledTime < _cooldownUntil) return;
            if (WaveBuilder.IsBattleRunning) return;
            if (WaveConfig.IsEmpty) return;

            _cooldownUntil = Time.unscaledTime + 2f;

            Plugin.Log.LogInfo("ArenaTrigger: hero entered the arena - starting your waves.");
            WaveBuilder.StartBattleNow();
        }

        /// <summary>
        /// Builds or repositions the trigger to cover the arena.
        /// </summary>
        public static void Ensure(BattleScene battle, Bounds area)
        {
            if (battle == null) return;

            Transform existing = battle.transform.Find(ObjectName);
            GameObject host;

            if (existing != null)
            {
                host = existing.gameObject;
            }
            else
            {
                host = new GameObject(ObjectName);
                host.transform.SetParent(battle.transform, false);

                // Borrow the battle object's layer: whatever the physics matrix lets
                // touch the hero there will let our trigger touch it too.
                host.layer = battle.gameObject.layer;

                host.AddComponent<ArenaTrigger>();
            }

            host.transform.position = area.center;

            BoxCollider2D box = host.GetComponent<BoxCollider2D>() ?? host.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(Mathf.Max(2f, area.size.x), Mathf.Max(4f, area.size.y));
            box.enabled = true;

            host.SetActive(true);

            Plugin.Log.LogInfo(
                $"ArenaTrigger: covering x {area.min.x:0.#}..{area.max.x:0.#}, " +
                $"y {area.min.y:0.#}..{area.max.y:0.#}.");
        }

        public static void SetEnabled(BattleScene battle, bool enabled)
        {
            if (battle == null) return;

            Transform existing = battle.transform.Find(ObjectName);
            if (existing == null) return;

            foreach (Collider2D col in existing.GetComponents<Collider2D>()) col.enabled = enabled;
        }
    }
}
