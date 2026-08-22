using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// Two bits of on-screen furniture the gauntlet needs: a fallback health/silk
    /// readout, and the end-of-run message.
    ///
    /// The readout is drawn by the mod rather than borrowed from the game. Silksong's
    /// own HUD lives on a separate camera driven by a PlayMaker slide FSM, and when
    /// that doesn't come up there's no reliable way to force it from outside without
    /// the side effects we've already been bitten by. Reading PlayerData and drawing
    /// the numbers ourselves can't fail in those ways - it's plainer than the real
    /// thing, but it's always there.
    /// </summary>
    internal static class GauntletHud
    {
        private static Texture2D _pixel;

        private static readonly Color Ink = new Color(0.95f, 0.93f, 0.88f, 1f);
        private static readonly Color Dim = new Color(0.62f, 0.60f, 0.56f, 1f);
        private static readonly Color Gold = new Color(0.85f, 0.72f, 0.42f, 1f);
        private static readonly Color MaskFull = new Color(0.96f, 0.95f, 0.92f, 1f);
        private static readonly Color MaskEmpty = new Color(1f, 1f, 1f, 0.14f);
        private static readonly Color SilkFull = new Color(0.94f, 0.86f, 0.55f, 1f);

        private static GUIStyle _label;
        private static GUIStyle _title;
        private static GUIStyle _body;
        private static GUIStyle _button;

        public static bool ShowReadout;

        // ------------------------------------------------------------------

        public static void Draw()
        {
            EnsureStyles();

            if (ShowReadout) DrawReadout();
            if (GauntletRun.ShowingResult && GauntletConfig.ShowClearMessage.Value) DrawResult();
        }

        private static void DrawReadout()
        {
            PlayerData pd = PlayerData.instance;
            if (pd == null) return;

            const float x = 26f;
            const float y = 24f;
            const float maskSize = 18f;
            const float maskGap = 5f;

            int max = Mathf.Max(0, pd.maxHealth);
            int current = Mathf.Clamp(pd.health, 0, max);

            for (int i = 0; i < max; i++)
            {
                var rect = new Rect(x + i * (maskSize + maskGap), y, maskSize, maskSize);
                Fill(rect, i < current ? MaskFull : MaskEmpty);
            }

            // Silk as a bar rather than orbs: the real HUD's spool is an animated
            // radial and approximating it badly would look worse than not trying.
            float barY = y + maskSize + 10f;
            float barWidth = Mathf.Max(60f, max * (maskSize + maskGap) - maskGap);
            var track = new Rect(x, barY, barWidth, 8f);
            Fill(track, MaskEmpty);

            int silkMax = Mathf.Max(1, pd.silkMax);
            float filled = Mathf.Clamp01(pd.silk / (float)silkMax);
            Fill(new Rect(track.x, track.y, track.width * filled, track.height), SilkFull);

            GUI.Label(new Rect(x, barY + 12f, 260f, 20f), $"{current}/{max} masks    {pd.silk}/{silkMax} silk", _label);
        }

        // ------------------------------------------------------------------

        private static void DrawResult()
        {
            const float width = 460f;
            const float height = 230f;

            var panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.45f, width, height);

            Fill(new Rect(0f, panel.y - 30f, Screen.width, height + 60f), new Color(0f, 0f, 0f, 0.55f));

            // A pair of hairlines instead of a box - closer to the game's own
            // full-width title cards than a floating window would be.
            Fill(new Rect(panel.x, panel.y, panel.width, 1f), Gold);
            Fill(new Rect(panel.x, panel.y + height, panel.width, 1f), Gold);

            GUI.Label(new Rect(panel.x, panel.y + 34f, panel.width, 40f), GauntletRun.ResultTitle, _title);
            GUI.Label(new Rect(panel.x, panel.y + 84f, panel.width, 60f), GauntletRun.ResultBody, _body);

            float buttonWidth = 170f;
            float buttonY = panel.y + height - 62f;

            var again = new Rect(panel.center.x - buttonWidth - 8f, buttonY, buttonWidth, 34f);
            var done = new Rect(panel.center.x + 8f, buttonY, buttonWidth, 34f);

            if (GUI.Button(again, "RUN IT AGAIN", _button)) GauntletRun.Restart();
            if (GUI.Button(done, "STAY HERE", _button)) GauntletRun.Dismiss();
        }

        // ------------------------------------------------------------------

        private static void Fill(Rect rect, Color colour)
        {
            EnsurePixel();
            Color saved = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _pixel);
            GUI.color = saved;
        }

        private static void EnsurePixel()
        {
            if (_pixel != null) return;

            _pixel = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();
        }

        private static void EnsureStyles()
        {
            if (_label != null) return;

            _label = new GUIStyle { fontSize = 12, alignment = TextAnchor.UpperLeft };
            _label.normal.textColor = Dim;

            _title = new GUIStyle
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _title.normal.textColor = Gold;

            _body = new GUIStyle
            {
                fontSize = 14,
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
            };
            _body.normal.textColor = Ink;

            _button = new GUIStyle
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 8, 8),
            };
            _button.normal.textColor = Ink;
            _button.hover.textColor = Gold;
            _button.active.textColor = Gold;
            _button.normal.background = Solid(new Color(0.14f, 0.13f, 0.12f, 0.95f));
            _button.hover.background = Solid(new Color(0.26f, 0.22f, 0.14f, 0.98f));
            _button.active.background = Solid(new Color(0.34f, 0.28f, 0.16f, 1f));
        }

        private static Texture2D Solid(Color colour)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixel(0, 0, colour);
            texture.Apply();
            return texture;
        }
    }
}
