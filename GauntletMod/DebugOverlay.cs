using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// Camera zoom, for looking at more of the arena than the game normally shows.
    ///
    /// This used to also draw collider outlines. That's gone: projecting every
    /// collider shape into screen space produced something too noisy to read and too
    /// inaccurate to trust, which is worse than nothing when you're using it to
    /// answer "is anything actually there". The room dump in F9 → Debug answers the
    /// same question with facts instead of guesswork.
    /// </summary>
    internal static class DebugOverlay
    {
        private static float _zoom = 1f;
        private static float _baseSize;
        private static Camera _camera;

        public static float Zoom => _zoom;

        public static void AdjustZoom(float delta)
        {
            _zoom = Mathf.Clamp(_zoom + delta, 1f, 8f);
            Plugin.Log.LogInfo($"DebugOverlay: zoom {_zoom:0.0}x.");
        }

        public static void ResetZoom() => _zoom = 1f;

        /// <summary>
        /// Reapplies the zoom every frame. The game drives its own orthographic size -
        /// room transitions, cutscenes and camera shakes all write to it - so setting
        /// it once wouldn't stick.
        /// </summary>
        public static void Tick()
        {
            Camera camera = ResolveCamera();
            if (camera == null || !camera.orthographic) return;

            if (Mathf.Approximately(_zoom, 1f))
            {
                // Back to normal - hand control back to the game and forget our base.
                if (_baseSize > 0f)
                {
                    camera.orthographicSize = _baseSize;
                    _baseSize = 0f;
                }
                return;
            }

            if (_baseSize <= 0f) _baseSize = camera.orthographicSize;

            float wanted = _baseSize * _zoom;
            if (!Mathf.Approximately(camera.orthographicSize, wanted)) camera.orthographicSize = wanted;
        }

        public static void Draw()
        {
            if (Mathf.Approximately(_zoom, 1f)) return;
            GUI.Label(new Rect(10f, Screen.height - 26f, 300f, 22f), $"Zoom {_zoom:0.0}x");
        }

        private static Camera ResolveCamera()
        {
            if (_camera != null) return _camera;

            _camera = (GameCameras.instance != null && GameCameras.instance.mainCamera != null)
                ? GameCameras.instance.mainCamera
                : Camera.main;

            return _camera;
        }

        public static void ForgetCamera()
        {
            _camera = null;
            _baseSize = 0f;
        }
    }
}
