using System;
using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// Noclip: Hornet hangs in the air with gravity and collision off, and you fly her
    /// around with the arrow keys or WASD. Same idea as Architect's free-move, and the
    /// fastest way to find a spot worth pinning as the home point.
    ///
    /// Three things have to be switched off together, or she won't behave:
    ///
    ///   - gravity, via HeroController.AffectedByGravity(false), which is the game's
    ///     own hook and correctly remembers the gravity scale to restore later
    ///   - the hero's control loop, via RelinquishControl(), or its state machine
    ///     fights every position we set
    ///   - physics simulation on the rigidbody, so terrain doesn't push her back out
    ///
    /// Everything is restored exactly on the way back out.
    /// </summary>
    internal static class FreeMove
    {
        private static bool _active;
        private static bool _wasSimulated = true;

        public static bool Active => _active;

        public static void Toggle()
        {
            if (_active) Disable(); else Enable();
        }

        public static void Enable()
        {
            if (_active) return;

            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                Plugin.Log.LogWarning("FreeMove: no hero to move - are you in a gameplay scene?");
                return;
            }

            try
            {
                hero.RelinquishControl();
                hero.AffectedByGravity(false);

                Rigidbody2D body = hero.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    _wasSimulated = body.simulated;
                    body.linearVelocity = Vector2.zero;

                    // simulated = false is the cleanest "no collision": it takes the
                    // body out of the physics world entirely, so transform moves pass
                    // straight through terrain and nothing pushes back.
                    body.simulated = false;
                }

                _active = true;
                Plugin.Log.LogInfo("FreeMove: on - arrows/WASD to fly, hold shift to go faster.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("FreeMove: couldn't enable: " + e);
                Disable();
            }
        }

        public static void Disable()
        {
            if (!_active) return;
            _active = false;

            HeroController hero = HeroController.instance;
            if (hero == null) return;

            try
            {
                Rigidbody2D body = hero.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    body.simulated = _wasSimulated;
                    body.linearVelocity = Vector2.zero;
                }

                hero.AffectedByGravity(true);
                hero.RegainControl();
                hero.AcceptInput();

                // RelinquishControl can take the HUD with it on the way in.
                ArenaSetup.ShowHud();

                Plugin.Log.LogInfo("FreeMove: off.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("FreeMove: couldn't cleanly disable: " + e);
            }
        }

        /// <summary>Call once per frame while the mode is on.</summary>
        public static void Tick()
        {
            if (!_active) return;

            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                Disable();
                return;
            }

            Vector2 direction = ReadDirection();
            if (direction == Vector2.zero) return;

            float speed = GauntletConfig.FreeMoveSpeed.Value;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) speed *= 3f;

            // Unscaled so flying still works with the wave panel's time freeze on.
            hero.transform.position += (Vector3)(direction.normalized * speed * Time.unscaledDeltaTime);
        }

        private static Vector2 ReadDirection()
        {
            var direction = Vector2.zero;

            if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) direction.x -= 1f;
            if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) direction.x += 1f;
            if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) direction.y += 1f;
            if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) direction.y -= 1f;

            return direction;
        }

        /// <summary>Small always-visible reminder, since the mode is easy to forget you left on.</summary>
        public static void DrawIndicator()
        {
            if (!_active) return;

            HeroController hero = HeroController.instance;
            string where = hero != null
                ? $"  ({hero.transform.position.x:0.#}, {hero.transform.position.y:0.#})"
                : "";

            var rect = new Rect(Screen.width - 330f, 10f, 320f, 24f);
            GUI.Label(rect, $"FREE MOVE — {GauntletConfig.FreeMoveKey.Value} to exit{where}");
        }
    }
}
