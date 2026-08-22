using System;
using System.Collections.Generic;
using UnityEngine;

namespace GauntletMod
{
    /// <summary>
    /// The wave editor window.
    ///
    /// This is IMGUI rather than a uGUI canvas, on purpose: IMGUI can draw the
    /// journal's atlased sprites directly, it needs no prefab work, and it can't be
    /// broken by a menu-layout change in a future game patch. The trade-off is that
    /// it looks like a tool window rather than part of Silksong - which is the right
    /// side of the trade for something you use between fights.
    ///
    /// Layout is three columns: the wave list, the selected wave's contents, and the
    /// enemy picker.
    /// </summary>
    internal static class GauntletPanel
    {
        private const int WindowId = 0x6A17;
        private const float TitleBarHeight = 34f;
        private const float IconSize = 34f;

        private static Rect _window = new Rect(80f, 60f, 940f, 580f);
        private static Vector2 _waveScroll;
        private static Vector2 _slotScroll;
        private static Vector2 _pickerScroll;

        private static int _selectedWave;
        private static string _search = "";
        private static bool _spawnableOnly;
        private static List<EnemyJournalRecord> _journalCache;
        private static string _status = "";
        private static float _savedTimeScale = 1f;


        private static Styles _styles;

        public static bool IsOpen { get; private set; }

        // ------------------------------------------------------------------

        public static void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        public static void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            _savedTimeScale = Time.timeScale;

            // Freeze the fight while you're editing it. Restored on close, and
            // clamped on the way back out so a zero can't get latched in.
            Time.timeScale = 0f;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            if (WaveConfig.Waves.Count == 0) WaveConfig.Load();
            _status = EnemyLibrary.Count == 0
                ? "No enemies harvested yet - visit the gauntlet room once to fill the library."
                : $"{EnemyLibrary.Count} enemy type(s) available.";
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;

            Time.timeScale = _savedTimeScale <= 0f ? 1f : _savedTimeScale;

            // Always hand the cursor back to the game rather than restoring whatever
            // it was when the panel opened. Capturing and restoring latched a visible
            // cursor into normal play: open the panel once while the cursor happened
            // to be showing and every later close put it back.
            HideCursor();

            SaveAndApply();
        }

        /// <summary>
        /// Writes the waves out and pushes them straight into the room.
        ///
        /// There's no reason to make "apply" a separate step you have to remember:
        /// editing the waves and wanting them live are the same intention. Closing the
        /// panel is enough - walk into the arena and they run.
        /// </summary>
        private static bool SaveApplyAndStart()
        {
            SaveAndApply();
            return WaveBuilder.CurrentBattle != null && WaveBuilder.StartBattleNow();
        }

        public static void SaveAndApply()
        {
            WaveConfig.Save();

            if (WaveBuilder.CurrentBattle == null) return;

            if (WaveConfig.IsEmpty)
            {
                WaveBuilder.Disarm();
                return;
            }

            WaveBuilder.Rebuild();
        }

        /// <summary>
        /// Puts the cursor back the way Silksong wants it during play. Safe to call
        /// every frame.
        /// </summary>
        /// <summary>The end-of-run overlay has buttons, so it needs a pointer too.</summary>
        public static void ShowCursorForOverlay()
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        public static void HideCursor()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.None;
        }

        public static void Draw()
        {
            if (!IsOpen) return;

            // The generated skin and textures are HideAndDontSave, but rebuild them
            // anyway if anything got collected - a null style here would throw every
            // frame.
            if (_styles == null || _styles.Skin == null) _styles = new Styles();

            // Keep the window on screen if the resolution changed under it.
            _window.width = Mathf.Min(_window.width, Screen.width - 20f);
            _window.height = Mathf.Min(_window.height, Screen.height - 20f);
            _window.x = Mathf.Clamp(_window.x, -_window.width + 120f, Screen.width - 120f);
            _window.y = Mathf.Clamp(_window.y, 0f, Screen.height - TitleBarHeight);

            GUI.skin = _styles.Skin;
            _window = GUI.Window(WindowId, _window, DrawWindow, GUIContent.none, _styles.Window);
            GUI.skin = null;
        }

        // ------------------------------------------------------------------

        private static void DrawWindow(int id)
        {
            DrawTitleBar();
            DrawRoomStatus();

            var body = new Rect(12f, TitleBarHeight + 30f, _window.width - 24f, _window.height - TitleBarHeight - 78f);
            GUILayout.BeginArea(body);
            GUILayout.BeginHorizontal();

            DrawWaveList(220f, body.height);
            GUILayout.Space(10f);
            DrawWaveContents(body.width - 220f - 320f - 20f, body.height);
            GUILayout.Space(10f);
            DrawEnemyPicker(320f, body.height);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            DrawFooter();

            // Dragging anywhere on the title bar, but not over the close button.
            GUI.DragWindow(new Rect(0f, 0f, _window.width - 44f, TitleBarHeight));
        }

        private static void DrawTitleBar()
        {
            var bar = new Rect(0f, 0f, _window.width, TitleBarHeight);
            GUI.DrawTexture(bar, _styles.TitleBg);
            GUI.Label(new Rect(14f, 0f, _window.width - 60f, TitleBarHeight), "CUSTOM GAUNTLET  ·  WAVE EDITOR", _styles.Title);

            var close = new Rect(_window.width - 34f, 7f, 21f, 21f);
            if (GUI.Button(close, "✕", _styles.CloseButton))
            {
                Close();
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// One line saying whether this room is actually under our control. Without
        /// it there's no way to tell "my waves are ready" from "I'm about to walk
        /// into the stock gauntlet", which is not a distinction you want to discover
        /// by walking in.
        /// </summary>
        private static void DrawRoomStatus()
        {
            var rect = new Rect(14f, TitleBarHeight + 4f, _window.width - 28f, 22f);

            bool adopted = WaveBuilder.CurrentBattle != null;
            int built = adopted ? (WaveBuilder.CurrentBattle.waves?.Count ?? 0) : 0;

            string text;
            bool good = false;

            if (!adopted)
            {
                text = "This room isn't under the mod's control. Use F9 -> 'Take over this room'.";
            }
            else if (EnemyLibrary.Count == 0)
            {
                text = "No enemies banked yet - the picker is empty. F9 -> 'Harvest Enemies In This Room'.";
            }
            else if (WaveBuilder.HasMissingEnemies)
            {
                List<string> missing = WaveBuilder.MissingEnemies();
                text = $"{missing.Count} still to load: " + string.Join(", ", missing.ToArray()) +
                       "   -   press Load missing.";
            }
            else if (built == 0)
            {
                text = $"Room taken over. {EnemyLibrary.Count} enemy type(s) available - " +
                       "add some to a wave and close this window.";
            }
            else
            {
                text = $"Ready: {built} wave(s) live. Close this and walk into the arena.";
                good = true;
            }

            GUI.Label(rect, text, good ? _styles.Status : _styles.WarnLabel);
        }

        private static void DrawWaveList(float width, float height)
        {
            GUILayout.BeginVertical(_styles.Panel, GUILayout.Width(width), GUILayout.Height(height));
            GUILayout.Label("WAVES", _styles.Header);

            _waveScroll = GUILayout.BeginScrollView(_waveScroll);

            for (int i = 0; i < WaveConfig.Waves.Count; i++)
            {
                Wave wave = WaveConfig.Waves[i];
                bool selected = i == _selectedWave;

                GUILayout.BeginHorizontal();

                string label = $"Wave {i + 1}   ({wave.TotalEnemies})";
                if (GUILayout.Button(label, selected ? _styles.RowSelected : _styles.Row))
                {
                    _selectedWave = i;
                }

                if (GUILayout.Button("▲", _styles.TinyButton, GUILayout.Width(24f)))
                {
                    WaveConfig.MoveWave(i, -1);
                    _selectedWave = Mathf.Max(0, i - 1);
                }
                if (GUILayout.Button("▼", _styles.TinyButton, GUILayout.Width(24f)))
                {
                    WaveConfig.MoveWave(i, 1);
                    _selectedWave = Mathf.Min(WaveConfig.Waves.Count - 1, i + 1);
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();

            if (GUILayout.Button("+  Add wave", _styles.Button))
            {
                WaveConfig.AddWave();
                _selectedWave = WaveConfig.Waves.Count - 1;
            }

            GUI.enabled = WaveConfig.Waves.Count > 0;
            if (GUILayout.Button("Remove wave " + (_selectedWave + 1), _styles.DangerButton))
            {
                WaveConfig.RemoveWave(_selectedWave);
                _selectedWave = Mathf.Clamp(_selectedWave, 0, Mathf.Max(0, WaveConfig.Waves.Count - 1));
            }
            GUI.enabled = true;

            GUILayout.EndVertical();
        }

        private static void DrawWaveContents(float width, float height)
        {
            GUILayout.BeginVertical(_styles.Panel, GUILayout.Width(width), GUILayout.Height(height));

            if (_selectedWave < 0 || _selectedWave >= WaveConfig.Waves.Count)
            {
                GUILayout.Label("IN THIS WAVE", _styles.Header);
                GUILayout.Label("Add a wave to get started.", _styles.Muted);
                GUILayout.EndVertical();
                return;
            }

            Wave wave = WaveConfig.Waves[_selectedWave];
            GUILayout.Label($"WAVE {_selectedWave + 1}", _styles.Header);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Start delay", _styles.Muted, GUILayout.Width(80f));
            wave.StartDelay = Mathf.Round(GUILayout.HorizontalSlider(wave.StartDelay, 0f, 10f) * 10f) / 10f;
            GUILayout.Label(wave.StartDelay.ToString("0.0") + "s", _styles.Muted, GUILayout.Width(42f));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            _slotScroll = GUILayout.BeginScrollView(_slotScroll);

            WaveSlot remove = null;
            foreach (WaveSlot slot in wave.Slots)
            {
                EnemyEntry entry = EnemyLibrary.Resolve(slot.EnemyId);

                GUILayout.BeginHorizontal(_styles.SlotRow, GUILayout.Height(IconSize + 8f));

                DrawIcon(entry?.Icon, IconSize);

                GUILayout.BeginVertical();
                GUILayout.FlexibleSpace();
                GUILayout.Label(entry != null ? entry.DisplayName : slot.Label + "   (needs loading)",
                    entry != null ? _styles.RowLabel : _styles.WarnLabel);
                GUILayout.FlexibleSpace();
                GUILayout.EndVertical();

                if (GUILayout.Button("–", _styles.TinyButton, GUILayout.Width(26f))) slot.Count = Mathf.Max(1, slot.Count - 1);
                GUILayout.Label("×" + slot.Count, _styles.Count, GUILayout.Width(34f));
                if (GUILayout.Button("+", _styles.TinyButton, GUILayout.Width(26f))) slot.Count = Mathf.Min(99, slot.Count + 1);

                GUILayout.Space(6f);
                if (GUILayout.Button("✕", _styles.TinyDanger, GUILayout.Width(26f))) remove = slot;

                GUILayout.EndHorizontal();
            }

            if (wave.Slots.Count == 0)
            {
                GUILayout.Label("Empty. Pick enemies from the list on the right.", _styles.Muted);
            }

            GUILayout.EndScrollView();

            if (remove != null) wave.Slots.Remove(remove);

            GUILayout.EndVertical();
        }

        private static void DrawEnemyPicker(float width, float height)
        {
            GUILayout.BeginVertical(_styles.Panel, GUILayout.Width(width), GUILayout.Height(height));

            GUILayout.BeginHorizontal();
            GUILayout.Label("ENEMIES", _styles.Header);
            GUILayout.FlexibleSpace();
            _spawnableOnly = GUILayout.Toggle(_spawnableOnly, "ready only", _styles.SmallButton);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Find", _styles.Muted, GUILayout.Width(34f));
            _search = GUILayout.TextField(_search ?? "", _styles.Search);
            if (GUILayout.Button("✕", _styles.TinyButton, GUILayout.Width(24f))) _search = "";
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            _pickerScroll = GUILayout.BeginScrollView(_pickerScroll);

            int shown = 0;
            int ready = 0;

            // The whole Hunter's Journal, whether or not we can spawn it yet.
            // EnemyJournalManager.GetAllEnemies returns every record regardless of
            // whether you've met it, so this is the complete bestiary - bosses
            // included - rather than only what happens to be loaded.
            foreach (EnemyJournalRecord record in AllJournalRecords())
            {
                string name = SafeName(record);
                if (!MatchesText(name, _search)) continue;

                EnemyEntry entry = EnemyLibrary.FindByRecord(record);
                if (_spawnableOnly && entry == null) continue;

                if (entry != null) ready++;
                shown++;

                DrawPickerRow(entry != null ? entry.Id : record.name, name, record.IconSprite, entry);
            }

            // Enemies we've banked that the journal has no entry for.
            foreach (EnemyEntry entry in EnemyLibrary.WithoutRecord)
            {
                if (!MatchesText(entry.DisplayName, _search)) continue;
                shown++;
                ready++;
                DrawPickerRow(entry.Id, entry.DisplayName, entry.Icon, entry);
            }

            if (shown == 0)
            {
                GUILayout.Label(
                    EnemyLibrary.Count == 0 && !_spawnableOnly
                        ? "The journal isn't loaded yet - start a run first."
                        : "Nothing matches that search.",
                    _styles.Muted);
            }

            GUILayout.EndScrollView();

            GUILayout.Label(
                $"{ready} of {shown} ready. Add any of them - greyed-out ones are fetched from their " +
                "own rooms when you press Load missing.",
                _styles.Muted);

            GUILayout.EndVertical();
        }

        private static void DrawPickerRow(string id, string name, Sprite icon, EnemyEntry entry)
        {
            bool ready = entry != null;

            GUILayout.BeginHorizontal(_styles.SlotRow, GUILayout.Height(IconSize + 8f));

            Color saved = GUI.color;
            if (!ready) GUI.color = new Color(1f, 1f, 1f, 0.4f);

            DrawIcon(icon, IconSize);

            GUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label(name, ready ? _styles.RowLabel : _styles.Muted);
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();

            GUI.color = saved;

            // Addable either way: pick the whole roster, then load what's missing.
            if (GUILayout.Button("Add", ready ? _styles.SmallButton : _styles.TinyButton, GUILayout.Width(50f)))
            {
                AddToSelectedWave(id, name);
            }

            GUILayout.EndHorizontal();
        }

        private static List<EnemyJournalRecord> AllJournalRecords()
        {
            if (_journalCache != null) return _journalCache;

            try
            {
                _journalCache = EnemyJournalManager.GetAllEnemies() ?? new List<EnemyJournalRecord>();
                _journalCache.Sort((a, b) => string.Compare(SafeName(a), SafeName(b), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: couldn't read the Hunter's Journal: " + e.Message);
                _journalCache = new List<EnemyJournalRecord>();
            }

            return _journalCache;
        }

        private static string SafeName(EnemyJournalRecord record)
        {
            if (record == null) return "";
            try
            {
                string name = record.DisplayName;
                return string.IsNullOrWhiteSpace(name) ? record.name : name;
            }
            catch
            {
                return record.name;
            }
        }

        private static bool MatchesText(string name, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return !string.IsNullOrEmpty(name) &&
                   name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DrawFooter()
        {
            var footer = new Rect(12f, _window.height - 42f, _window.width - 24f, 34f);
            GUILayout.BeginArea(footer);
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Save", _styles.Button, GUILayout.Width(90f)))
            {
                SaveAndApply();
                _status = "Saved and applied to the room.";
            }

            if (GUILayout.Button("Reload", _styles.Button, GUILayout.Width(90f)))
            {
                WaveConfig.Load();
                _selectedWave = 0;
                _status = "Reloaded from file.";
            }

            GUILayout.Space(10f);

            bool missingAny = WaveBuilder.HasMissingEnemies;

            GUI.enabled = missingAny && !EnemyImporter.Busy;
            if (GUILayout.Button(EnemyImporter.Busy ? "Loading..." : "Load missing", _styles.Button, GUILayout.Width(130f)))
            {
                WaveConfig.Save();
                Plugin.Instance.StartCoroutine(EnemyImporter.ImportMissing());
                _status = "Fetching your enemies from their own rooms...";
            }
            GUI.enabled = true;

            GUI.enabled = !missingAny;
            if (GUILayout.Button("▶  START NOW", _styles.StartButton, GUILayout.Width(150f)))
            {
                if (WaveConfig.IsEmpty)
                {
                    _status = "No enemies in any wave yet - add some from the list on the right first.";
                }
                else if (SaveApplyAndStart())
                {
                    _status = "Fight started.";
                    Close();
                }
                else
                {
                    _status = "Couldn't start - see the log.";
                }
            }
            GUI.enabled = true;

            GUILayout.Space(10f);

            // The arena and the bench are opposite ends of one long room, so getting
            // between them is a walk you'd otherwise do a lot.
            if (GUILayout.Button("To arena", _styles.Button, GUILayout.Width(90f)))
            {
                ArenaSetup.MoveHeroTo(ArenaSetup.ArenaPoint(), "panel teleport");
                _status = "Moved to the arena.";
                Close();
            }

            if (GUILayout.Button("To bench", _styles.Button, GUILayout.Width(90f)))
            {
                ArenaSetup.MoveHeroTo(ArenaSetup.HomePoint(), "panel teleport");
                _status = "Moved to the bench.";
                Close();
            }

            GUILayout.Space(12f);
            GUILayout.Label(_status ?? "", _styles.Status);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------

        private static void AddToSelectedWave(string id, string displayName)
        {
            if (WaveConfig.Waves.Count == 0)
            {
                WaveConfig.AddWave();
                _selectedWave = 0;
            }
            _selectedWave = Mathf.Clamp(_selectedWave, 0, WaveConfig.Waves.Count - 1);

            Wave wave = WaveConfig.Waves[_selectedWave];

            // Adding the same enemy twice bumps the count rather than making a
            // second row - much less fiddly when you're building a wave of six.
            foreach (WaveSlot existing in wave.Slots)
            {
                if (existing.EnemyId == id)
                {
                    existing.Count = Mathf.Min(99, existing.Count + 1);
                    _status = $"{displayName} ×{existing.Count} in wave {_selectedWave + 1}.";
                    return;
                }
            }

            wave.Slots.Add(new WaveSlot(id, displayName, 1));
            _status = $"Added {displayName} to wave {_selectedWave + 1}.";
        }

        private static bool Matches(EnemyEntry entry, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return entry.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                   || entry.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Journal icons are packed into an atlas, so drawing one means mapping the
        /// sprite's pixel rect into normalised texture coordinates rather than just
        /// blitting the texture.
        /// </summary>
        private static void DrawIcon(Sprite sprite, float size)
        {
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));

            if (sprite == null)
            {
                GUI.DrawTexture(rect, _styles.IconPlaceholder);
                return;
            }

            try
            {
                Texture2D texture = sprite.texture;
                if (texture == null)
                {
                    GUI.DrawTexture(rect, _styles.IconPlaceholder);
                    return;
                }

                Rect tr = sprite.textureRect;
                var coords = new Rect(
                    tr.x / texture.width,
                    tr.y / texture.height,
                    tr.width / texture.width,
                    tr.height / texture.height);

                GUI.DrawTextureWithTexCoords(rect, texture, coords, alphaBlend: true);
            }
            catch
            {
                GUI.DrawTexture(rect, _styles.IconPlaceholder);
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// IMGUI's default skin is the grey Unity editor look, so everything gets
        /// restyled here. Textures are generated in code - no asset bundle needed.
        /// </summary>
        private class Styles
        {
            private static readonly Color Ink = new Color(0.91f, 0.89f, 0.85f);
            private static readonly Color Dim = new Color(0.62f, 0.60f, 0.58f);
            private static readonly Color Accent = new Color(0.85f, 0.72f, 0.42f);

            public readonly GUISkin Skin;
            public readonly Texture2D TitleBg;
            public readonly Texture2D IconPlaceholder;

            public readonly GUIStyle Window;
            public readonly GUIStyle Panel;
            public readonly GUIStyle Title;
            public readonly GUIStyle Header;
            public readonly GUIStyle Muted;
            public readonly GUIStyle Status;
            public readonly GUIStyle RowLabel;
            public readonly GUIStyle WarnLabel;
            public readonly GUIStyle Count;
            public readonly GUIStyle Row;
            public readonly GUIStyle RowSelected;
            public readonly GUIStyle SlotRow;
            public readonly GUIStyle Button;
            public readonly GUIStyle StartButton;
            public readonly GUIStyle SmallButton;
            public readonly GUIStyle TinyButton;
            public readonly GUIStyle DangerButton;
            public readonly GUIStyle TinyDanger;
            public readonly GUIStyle CloseButton;
            public readonly GUIStyle Search;

            public Styles()
            {
                Skin = UnityEngine.Object.Instantiate(GUI.skin);
                Skin.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(Skin);

                Texture2D windowBg = Solid(new Color(0.08f, 0.08f, 0.10f, 0.97f));
                Texture2D panelBg = Solid(new Color(0.13f, 0.13f, 0.16f, 0.95f));
                Texture2D rowBg = Solid(new Color(0.17f, 0.17f, 0.20f, 0.9f));
                Texture2D rowHover = Solid(new Color(0.23f, 0.23f, 0.27f, 0.95f));
                Texture2D rowActive = Solid(new Color(0.30f, 0.26f, 0.16f, 0.98f));
                Texture2D dangerBg = Solid(new Color(0.32f, 0.14f, 0.14f, 0.95f));
                Texture2D dangerHover = Solid(new Color(0.45f, 0.18f, 0.18f, 0.98f));

                TitleBg = Solid(new Color(0.05f, 0.05f, 0.07f, 0.99f));
                IconPlaceholder = Solid(new Color(1f, 1f, 1f, 0.06f));

                Window = new GUIStyle { normal = { background = windowBg }, border = new RectOffset(2, 2, 2, 2) };

                Panel = new GUIStyle
                {
                    normal = { background = panelBg },
                    padding = new RectOffset(10, 10, 8, 10),
                };

                Title = Label(15, FontStyle.Bold, Accent, TextAnchor.MiddleLeft);
                Header = Label(12, FontStyle.Bold, Dim, TextAnchor.MiddleLeft);
                Header.margin = new RectOffset(0, 0, 0, 8);
                Muted = Label(12, FontStyle.Normal, Dim, TextAnchor.MiddleLeft);
                Muted.wordWrap = true;
                Status = Label(12, FontStyle.Normal, Dim, TextAnchor.MiddleLeft);
                RowLabel = Label(13, FontStyle.Normal, Ink, TextAnchor.MiddleLeft);
                WarnLabel = Label(13, FontStyle.Normal, new Color(0.85f, 0.55f, 0.4f), TextAnchor.MiddleLeft);
                Count = Label(13, FontStyle.Bold, Ink, TextAnchor.MiddleCenter);

                Row = Button13(rowBg, rowHover, rowActive, Ink);
                Row.alignment = TextAnchor.MiddleLeft;
                Row.padding = new RectOffset(10, 6, 6, 6);

                RowSelected = new GUIStyle(Row);
                RowSelected.normal.background = rowActive;
                RowSelected.normal.textColor = Accent;

                SlotRow = new GUIStyle
                {
                    normal = { background = rowBg },
                    padding = new RectOffset(6, 6, 4, 4),
                    margin = new RectOffset(0, 0, 0, 4),
                };

                Button = Button13(rowBg, rowHover, rowActive, Ink);
                Button.padding = new RectOffset(10, 10, 7, 7);
                Button.margin = new RectOffset(0, 6, 2, 2);

                // The one button people are actually looking for, so it gets the accent.
                StartButton = Button13(
                    Solid(new Color(0.34f, 0.28f, 0.12f, 0.98f)),
                    Solid(new Color(0.48f, 0.39f, 0.16f, 1f)),
                    Solid(new Color(0.55f, 0.45f, 0.18f, 1f)),
                    new Color(1f, 0.93f, 0.75f));
                StartButton.fontSize = 14;
                StartButton.fontStyle = FontStyle.Bold;
                StartButton.padding = new RectOffset(10, 10, 7, 7);

                SmallButton = new GUIStyle(Button) { fontSize = 12, padding = new RectOffset(6, 6, 5, 5) };
                TinyButton = new GUIStyle(Button) { fontSize = 13, padding = new RectOffset(0, 0, 3, 3) };
                TinyButton.alignment = TextAnchor.MiddleCenter;

                DangerButton = Button13(dangerBg, dangerHover, dangerHover, Ink);
                DangerButton.padding = new RectOffset(10, 10, 7, 7);

                TinyDanger = new GUIStyle(DangerButton) { fontSize = 12, padding = new RectOffset(0, 0, 3, 3) };
                TinyDanger.alignment = TextAnchor.MiddleCenter;

                CloseButton = Button13(Solid(new Color(0.25f, 0.10f, 0.10f, 0.9f)), dangerHover, dangerHover, Ink);
                CloseButton.fontSize = 13;
                CloseButton.alignment = TextAnchor.MiddleCenter;
                CloseButton.padding = new RectOffset(0, 0, 1, 1);

                Search = new GUIStyle(Skin.textField)
                {
                    fontSize = 13,
                    padding = new RectOffset(6, 6, 5, 5),
                };
                Search.normal.textColor = Ink;
                Search.focused.textColor = Ink;

                Skin.verticalScrollbar = new GUIStyle(Skin.verticalScrollbar);
                Skin.label = RowLabel;
            }

            private static GUIStyle Label(int size, FontStyle style, Color colour, TextAnchor anchor)
            {
                var result = new GUIStyle
                {
                    fontSize = size,
                    fontStyle = style,
                    alignment = anchor,
                    padding = new RectOffset(2, 2, 2, 2),
                };
                result.normal.textColor = colour;
                return result;
            }

            private static GUIStyle Button13(Texture2D normal, Texture2D hover, Texture2D active, Color colour)
            {
                var result = new GUIStyle
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(8, 8, 6, 6),
                    margin = new RectOffset(0, 4, 2, 2),
                };
                result.normal.background = normal;
                result.normal.textColor = colour;
                result.hover.background = hover;
                result.hover.textColor = colour;
                result.active.background = active;
                result.active.textColor = colour;
                result.focused.background = hover;
                result.focused.textColor = colour;
                return result;
            }

            private static Texture2D Solid(Color colour)
            {
                var texture = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
                texture.SetPixel(0, 0, colour);
                texture.Apply();
                return texture;
            }
        }
    }
}
