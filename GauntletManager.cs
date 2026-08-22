using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GauntletMod
{
    /// <summary>
    /// The mod's one persistent MonoBehaviour. It watches scene changes so the right
    /// thing happens at the right moment, and hosts both the wave editor and the
    /// small dev menu.
    /// </summary>
    public class GauntletManager : MonoBehaviour
    {
        private const string IntroScene = "Pre_Menu_Intro";
        private const string TitleScene = "Menu_Title";

        /// <summary>
        /// Every shipped scene that carries a BattleScene with more than one wave -
        /// i.e. the game's actual multi-wave arenas, found by scanning all 590 scene
        /// bundles. Hang_04 is the Grand Forum; the rest are here so a wrong guess
        /// costs one click rather than a round trip.
        /// </summary>
        private static readonly string[] KnownArenas =
        {
            "Hang_04",        // Grand Forum - 12 waves, in Hang_04_boss
            "Library_02",     // High Halls - 11 wave objects
            "Under_18",
            "Dust_03",
            "Song_04",
            "Clover_04b",
            "Greymoor_20c",
        };

        private bool _devMenuOpen;
        private string _status = "";
        private string _detail = "";
        private string _sceneField = "";

        private Coroutine _menuInjectLoop;

        // If the game is built with "Active Input Handling = Input System (New)"
        // only, every UnityEngine.Input call throws. Probe once rather than throwing
        // every frame; the mod is fully usable without the hotkeys.
        private bool _legacyInputUsable = true;
        private bool _wasDead;

        private void Awake()
        {
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        }

        private void Start()
        {
            // We may already be sitting in the logo scene by the time BepInEx has
            // loaded us.
            HandleScene(SceneManager.GetActiveScene().name);
        }

        private void OnActiveSceneChanged(Scene from, Scene to)
        {
            try { HandleScene(to.name); }
            catch (Exception e) { Plugin.Log.LogError("Custom Gauntlet: scene handler failed: " + e); }
        }

        private void HandleScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;

            if (sceneName == IntroScene)
            {
                if (GauntletConfig.SkipStartupLogos.Value) StartCoroutine(SkipStartupLogos());
                return;
            }

            if (sceneName == TitleScene)
            {
                // Back at the menu: the play-mode screen is a fresh instance, and any
                // in-progress gauntlet is over.
                GauntletMode.Reset();
                GauntletRun.Reset();
                MenuInjector.Forget();
                DebugOverlay.ForgetCamera();
                if (GauntletPanel.IsOpen) GauntletPanel.Close();

                // Validate the configured room here rather than at the moment of
                // truth: Addressables is definitely up by the title screen, and a bad
                // name gets repaired (loudly) before it can hang a load.
                SceneCatalog.ResolveGauntletScene();

                if (_menuInjectLoop != null) StopCoroutine(_menuInjectLoop);
                _menuInjectLoop = StartCoroutine(InjectMenuButtonWhenReady());
                return;
            }

            GauntletMode.ReArmIfGauntletSave();

            if (GauntletMode.Active && GauntletMode.IsGauntletRoom(sceneName))
            {
                StartCoroutine(GauntletMode.OnRoomLoaded());
                StartWatchdog();
            }
            else if (GauntletConfig.HarvestEnemies.Value)
            {
                // Not a gauntlet room, but any room is a chance to bank enemies for
                // the picker.
                StartCoroutine(HarvestAfterSettle());
            }
        }

        private Coroutine _watchdog;

        private void StartWatchdog()
        {
            if (_watchdog == null) _watchdog = StartCoroutine(RoomWatchdog());
        }

        /// <summary>
        /// Two jobs, both on a half-second timer, for as long as the gauntlet is on.
        ///
        /// **Re-setup.** Dying reloads the room, and everything we did to it - cleared
        /// enemies, adopted battles, built waves - dies with it. Scene-change events
        /// don't reliably fire for a same-scene reload, so this notices instead: if
        /// we're standing in the gauntlet room and the BattleScene we adopted has
        /// evaporated, the room came back without us and needs setting up again. That
        /// was why dying left the arena empty.
        ///
        /// **Sweeping.** Deleting any enemy we didn't spawn, because the room's own
        /// FSMs can bring the stock fight back in after every setup pass has run.
        /// </summary>
        private IEnumerator RoomWatchdog()
        {
            int reported = 0;

            while (GauntletMode.Active)
            {
                yield return new WaitForSeconds(0.5f);

                if (!GauntletMode.Active) break;
                if (GauntletMode.SettingUp) continue;
                if (SceneManager.GetActiveScene().name != GauntletMode.GauntletScene) continue;

                if (GauntletMode.RoomReady && WaveBuilder.CurrentBattle == null)
                {
                    Plugin.Log.LogInfo("Custom Gauntlet: the room reloaded under us - setting it up again.");
                    yield return GauntletMode.OnRoomLoaded();
                    continue;
                }

                // Checked before the RoomReady gate: being out of bounds is worth
                // fixing even while the room is still setting itself up.
                if (GauntletConfig.RescueOutOfBounds.Value && ArenaSetup.RescueIfOutOfBounds()) continue;

                if (!GauntletMode.RoomReady) continue;

                // Burrowers that dug in and never came back up.
                WaveBuilder.UnstickBuriedEnemies();

                if (!GauntletConfig.KeepRoomClear.Value) continue;

                int removed = ArenaSetup.SweepForeignEnemies();
                if (removed > 0)
                {
                    reported += removed;
                    // Only log the first few, or a room that keeps respawning things
                    // would bury everything else in the log.
                    if (reported <= 40)
                    {
                        Plugin.Log.LogInfo($"Custom Gauntlet: swept {removed} enemy/enemies the room tried to bring back.");
                    }
                }
            }

            _watchdog = null;
        }

        private IEnumerator HarvestAfterSettle()
        {
            yield return new WaitForSeconds(0.5f);
            EnemyLibrary.HarvestActiveScene();
        }

        /// <summary>
        /// Pre_Menu_Intro is the studio-logo scene; it hands off to Menu_Title from a
        /// PlayMaker FSM rather than from code, so the reliable way past it is to
        /// load the title screen ourselves once the GameManager is up.
        /// </summary>
        private IEnumerator SkipStartupLogos()
        {
            float elapsed = 0f;
            while (SceneManager.GetActiveScene().name == IntroScene && elapsed < 5f)
            {
                elapsed += Time.unscaledDeltaTime;

                if (elapsed > 0.5f && GameManager.instance != null)
                {
                    Plugin.Log.LogInfo("Custom Gauntlet: skipping startup logo sequence.");
                    GameManager.instance.LoadScene(TitleScene);
                    yield break;
                }

                yield return null;
            }
        }

        /// <summary>
        /// UIManager and its play-mode screen aren't wired up the instant the title
        /// scene becomes active, so poll briefly rather than assuming.
        /// </summary>
        private IEnumerator InjectMenuButtonWhenReady()
        {
            float elapsed = 0f;
            while (elapsed < 15f && SceneManager.GetActiveScene().name == TitleScene)
            {
                MenuInjector.TryInject();
                elapsed += 0.5f;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            _menuInjectLoop = null;
        }

        // ------------------------------------------------------------------

        private void Update()
        {
            if (!_legacyInputUsable) return;

            try
            {
                if (Input.GetKeyDown(GauntletConfig.PanelKey.Value)) GauntletPanel.Toggle();
                if (Input.GetKeyDown(GauntletConfig.DevMenuKey.Value)) _devMenuOpen = !_devMenuOpen;
                if (Input.GetKeyDown(GauntletConfig.FreeMoveKey.Value)) FreeMove.Toggle();
                if (Input.GetKeyDown(GauntletConfig.ReadoutKey.Value)) GauntletHud.ShowReadout = !GauntletHud.ShowReadout;
                if (Input.GetKeyDown(GauntletConfig.ZoomOutKey.Value)) DebugOverlay.AdjustZoom(0.5f);
                if (Input.GetKeyDown(GauntletConfig.ZoomInKey.Value)) DebugOverlay.AdjustZoom(-0.5f);

                FreeMove.Tick();
                GauntletRun.UpdateEquipFreedom();
                GuardAgainstLeakedUiState();
            }
            catch (Exception e)
            {
                _legacyInputUsable = false;
                Plugin.Log.LogWarning("Custom Gauntlet: legacy input unavailable, hotkeys disabled: " + e.Message);
            }
        }

        private void LateUpdate()
        {
            DebugOverlay.Tick();
            WatchForDeath();
        }

        /// <summary>
        /// Puts Hornet back on the exact configured home coordinates after any death.
        ///
        /// Every previous attempt tried to make the *game's* respawn land in the right
        /// place - patching the marker lookup, the entry point, the hazard location.
        /// Each covered one route and missed another. This ignores the question
        /// entirely: watch for the death flag clearing, then put her where she belongs.
        /// </summary>
        private void WatchForDeath()
        {
            HeroController hero = HeroController.instance;
            if (hero == null || hero.cState == null) return;

            bool dead = hero.cState.dead;

            if (_wasDead && !dead && GauntletMode.Active && GauntletMode.InGauntletRoom)
            {
                StartCoroutine(SnapHomeAfterRespawn());
            }

            _wasDead = dead;
        }

        private IEnumerator SnapHomeAfterRespawn()
        {
            // Dying restarts the gauntlet rather than resuming it. Done before the
            // repositioning loop so the room is already rebuilt by the time you're
            // standing at the bench again.
            GauntletRun.ResetArena("a death");

            // Repeated rather than once: the respawn sequence keeps positioning her
            // for a while after the death flag clears, so a single move gets undone.
            for (int i = 0; i < 6; i++)
            {
                yield return new WaitForSeconds(0.15f);

                if (!GauntletMode.Active) yield break;
                if (!ArenaSetup.TryHomePoint(out Vector3 home)) yield break;

                if (Vector3.Distance(HeroController.instance.transform.position, home) > 1.5f)
                {
                    ArenaSetup.MoveHeroTo(home, "respawn after death");
                }
            }
        }

        /// <summary>
        /// Undoes anything the wave panel left behind. Both the frozen clock and the
        /// visible cursor are things the panel legitimately sets while it's open, and
        /// both are ruinous if they outlive it - a stuck timeScale of zero looks
        /// exactly like the game hanging.
        /// </summary>
        private void GuardAgainstLeakedUiState()
        {
            // Both of the mod's menus are mouse-driven, so the cursor has to be there
            // while either is open - hiding it unconditionally made the dev menu
            // impossible to click.
            if (GauntletPanel.IsOpen || _devMenuOpen || GauntletRun.ShowingResult)
            {
                if (!Cursor.visible)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                return;
            }

            GameManager gm = GameManager.instance;
            if (gm == null || gm.IsMenuScene()) return;

            if (Cursor.visible) GauntletPanel.HideCursor();

            if (Time.timeScale == 0f && !gm.isPaused)
            {
                Plugin.Log.LogWarning("Custom Gauntlet: time was still frozen with no menu open - unfreezing.");
                Time.timeScale = 1f;
            }
        }

        private void OnGUI()
        {
            DebugOverlay.Draw();
            GauntletHud.Draw();
            FreeMove.DrawIndicator();
            GauntletPanel.Draw();
            if (_devMenuOpen) DrawDevMenu();
        }

        private enum DevTab { Play, Room, Waves, Debug }

        private const int DevWindowId = 0x6A18;
        private const float ResizeGrip = 16f;

        private DevTab _tab = DevTab.Play;
        private Vector2 _devScroll;
        private Rect _devWindow = new Rect(20f, 20f, 420f, 460f);
        private bool _resizingDev;

        /// <summary>
        /// The dev menu, in tabs. It grew a button at a time while chasing bugs and
        /// became a wall; grouping by what you're trying to do makes it usable again.
        /// </summary>
        private void DrawDevMenu()
        {
            _devWindow.width = Mathf.Clamp(_devWindow.width, 320f, Screen.width - 20f);
            _devWindow.height = Mathf.Clamp(_devWindow.height, 220f, Screen.height - 20f);
            _devWindow.x = Mathf.Clamp(_devWindow.x, -_devWindow.width + 120f, Screen.width - 120f);
            _devWindow.y = Mathf.Clamp(_devWindow.y, 0f, Screen.height - 40f);

            _devWindow = GUI.Window(DevWindowId, _devWindow, DrawDevWindow, "Custom Gauntlet");
        }

        /// <summary>
        /// Drag it by the title bar, resize it from the bottom-right corner. IMGUI
        /// gives you dragging for free but has no resize handle, so that half is a
        /// grip rect the mouse drags against.
        /// </summary>
        private void DrawDevWindow(int id)
        {
            HandleDevResize();

            GUILayout.Space(2);

            GUILayout.Label($"<b>You are in:  {SceneManager.GetActiveScene().name}</b>");
            GUILayout.Label("   loaded: " + ArenaSetup.DescribeLoadedScenes());
            GUILayout.Space(4);

            GUILayout.Label("<b>Custom Gauntlet</b>   " +
                            $"room {GauntletConfig.GauntletScene.Value} · " +
                            $"{(GauntletMode.RoomReady ? "ready" : GauntletMode.Active ? "setting up" : "inactive")} · " +
                            $"{EnemyLibrary.Count} enemies banked");

            GUILayout.BeginHorizontal();
            foreach (DevTab tab in new[] { DevTab.Play, DevTab.Room, DevTab.Waves, DevTab.Debug })
            {
                bool on = _tab == tab;
                if (GUILayout.Toggle(on, tab.ToString(), GUI.skin.button) && !on) _tab = tab;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _devScroll = GUILayout.BeginScrollView(_devScroll);
            switch (_tab)
            {
                case DevTab.Play: DrawPlayTab(); break;
                case DevTab.Room: DrawRoomTab(); break;
                case DevTab.Waves: DrawWavesTab(); break;
                case DevTab.Debug: DrawDebugTab(); break;
            }
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            if (!string.IsNullOrEmpty(_detail)) GUILayout.Label(_detail);

            GUI.Label(new Rect(_devWindow.width - ResizeGrip - 4f, _devWindow.height - ResizeGrip - 2f,
                ResizeGrip, ResizeGrip), "◢");

            // Drag anywhere on the title strip, but not over the resize grip.
            GUI.DragWindow(new Rect(0f, 0f, _devWindow.width, 20f));
        }

        private void HandleDevResize()
        {
            var grip = new Rect(_devWindow.width - ResizeGrip - 4f, _devWindow.height - ResizeGrip - 2f,
                ResizeGrip + 4f, ResizeGrip + 2f);

            Event e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition))
            {
                _resizingDev = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                _resizingDev = false;
            }
            else if (_resizingDev && e.type == EventType.MouseDrag)
            {
                _devWindow.width = Mathf.Max(320f, e.mousePosition.x + ResizeGrip * 0.5f);
                _devWindow.height = Mathf.Max(220f, e.mousePosition.y + ResizeGrip * 0.5f);
                e.Use();
            }
        }

        private void DrawPlayTab()
        {
            if (GUILayout.Button($"Free move: {(FreeMove.Active ? "ON" : "off")}   ({GauntletConfig.FreeMoveKey.Value})"))
            {
                FreeMove.Toggle();
                _devMenuOpen = false;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("→ Arena"))
            {
                ArenaSetup.MoveHeroTo(ArenaSetup.ArenaPoint(), "teleport to arena");
                _devMenuOpen = false;
            }
            if (GUILayout.Button("→ Bench"))
            {
                ArenaSetup.MoveHeroTo(ArenaSetup.HomePoint(), "teleport to home point");
                _devMenuOpen = false;
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Apply full loadout (stats + all tools)"))
            {
                PlayerBuffs.ApplyFullLoadout();
                _status = "Loadout applied.";
            }

            if (GUILayout.Button($"Masks/silk readout: {(GauntletHud.ShowReadout ? "ON" : "off")}   ({GauntletConfig.ReadoutKey.Value})"))
            {
                GauntletHud.ShowReadout = !GauntletHud.ShowReadout;
            }

            if (GUILayout.Button("Force the HUD back on"))
            {
                ArenaSetup.ShowHud(verbose: true);
                _status = "HUD forced - see the log for what state it was in.";
            }

            if (GUILayout.Button("Start Custom Gauntlet (title menu only)"))
            {
                if (GameManager.instance != null && GameManager.instance.IsMenuScene())
                {
                    GauntletMode.StartFromMenu();
                    _devMenuOpen = false;
                }
                else
                {
                    _status = "Only works from the title menu.";
                }
            }
        }

        private void DrawRoomTab()
        {
            if (GUILayout.Button("Let me out (release camera lock + gates + doors)"))
            {
                ArenaSetup.ReleaseArenaLocks();
                ArenaSetup.UnsealExits();
                _status = "Arena locks released - the room's own doors should work now.";
            }

            if (GUILayout.Button("Take over this room (clear stock gauntlet)"))
            {
                TakeOverRoom();
            }

            if (GUILayout.Button("Put the bench where I'm standing"))
            {
                StartCoroutine(PlaceBenchAtHero());
                _status = "Placing the bench - see the log.";
            }

            GUILayout.Space(6);
            GUILayout.Label($"Home point: {(GauntletConfig.HomePointSet.Value ? $"({GauntletConfig.HomePointX.Value:0.#}, {GauntletConfig.HomePointY.Value:0.#})" : "auto")}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Set here"))
            {
                ArenaSetup.PinHomePointHere();
                _status = "Home point pinned.";
            }
            if (GUILayout.Button("Back to auto"))
            {
                GauntletConfig.HomePointSet.Value = false;
                _status = "Home point back to automatic.";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("Doors in this room lead to:");
            GUILayout.Label("   " + ArenaSetup.DescribeDoorTargets());
            GUILayout.Label("   (the mod no longer writes these - this is the room's own wiring)");

            GUILayout.Space(6);
            if (GUILayout.Button("Use this room as the gauntlet"))
            {
                CaptureCurrentScene();
            }

            GUILayout.BeginHorizontal();
            _sceneField = GUILayout.TextField(_sceneField ?? "", GUILayout.Width(270f));
            if (GUILayout.Button("Set", GUILayout.Width(48f)))
            {
                string name = _sceneField.Trim();
                if (SceneCatalog.Exists(name))
                {
                    GauntletConfig.GauntletScene.Value = name;
                    _status = $"Gauntlet room set to '{name}'.";
                }
                else
                {
                    _status = $"'{name}' isn't a scene in this build.";
                }
            }
            GUILayout.EndHorizontal();



            GUILayout.Space(6);
            GUILayout.Label("Known wave arenas:");
            foreach (string candidate in KnownArenas)
            {
                if (GUILayout.Button("   " + candidate))
                {
                    _sceneField = candidate;
                    GauntletConfig.GauntletScene.Value = candidate;
                    _status = $"Gauntlet room set to '{candidate}'. Start a run to go there.";
                }
            }
        }

        private void DrawWavesTab()
        {
            if (GUILayout.Button($"Open the wave editor ({GauntletConfig.PanelKey.Value})"))
            {
                GauntletPanel.Open();
                _devMenuOpen = false;
            }

            if (GUILayout.Button("Harvest enemies in this room"))
            {
                int added = EnemyLibrary.HarvestActiveScene();
                _status = added > 0 ? $"Banked {added} new enemy type(s)." : "Nothing new here.";
            }


            GUI.enabled = !EnemyImporter.Busy;
            if (GUILayout.Button("Load every enemy my waves need"))
            {
                StartCoroutine(EnemyImporter.ImportMissing());
                _status = "Fetching your enemies from their own rooms...";
            }
            GUI.enabled = true;

            if (EnemyImporter.ParkedRoomCount > 0)
            {
                GUILayout.Label($"Rooms held in memory for imported enemies: {EnemyImporter.ParkedRoomCount}");
            }

            if (!string.IsNullOrEmpty(EnemyImporter.LastResult)) GUILayout.Label(EnemyImporter.LastResult);

            GUILayout.Space(6);
            if (GUILayout.Button("Nuke every enemy in this room"))
            {
                ArenaSetup.StripRoom(includeInactive: true);
                _status = "Cleared, dormant enemies included.";
            }

            if (GUILayout.Button("Where are my enemies? (log + go to them)"))
            {
                WaveBuilder.ReportPlacements();
                Vector3? at = WaveBuilder.FirstSpawnedPosition();
                if (at.HasValue)
                {
                    ArenaSetup.MoveHeroTo(at.Value, "jump to first spawned enemy");
                    _status = $"Moved to ({at.Value.x:0.#}, {at.Value.y:0.#}).";
                    _devMenuOpen = false;
                }
                else
                {
                    _status = "Nothing spawned - waves empty, or enemies not in the library.";
                }
            }
        }

        private void DrawDebugTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Zoom {DebugOverlay.Zoom:0.0}x", GUILayout.Width(80f));
            if (GUILayout.Button("−")) DebugOverlay.AdjustZoom(0.5f);
            if (GUILayout.Button("+")) DebugOverlay.AdjustZoom(-0.5f);
            if (GUILayout.Button("Reset")) DebugOverlay.ResetZoom();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (GUILayout.Button("Dump room contents to log"))
            {
                ArenaSetup.DumpRoom();
                _status = "Written to LogOutput.log - search for 'room dump'.";
            }

            if (GUILayout.Button("List gates (and where they lead)"))
            {
                _detail = ArenaSetup.DescribeGates();
                Plugin.Log.LogInfo("Custom Gauntlet: " + _detail);
            }

            if (GUILayout.Button("List respawn markers / benches"))
            {
                _detail = ArenaSetup.DescribeSpawns();
                Plugin.Log.LogInfo("Custom Gauntlet: " + _detail);
            }

        }

        /// <summary>
        /// Drops the bench at the player's feet and repoints respawn at it, so dying
        /// brings you back to the bench you just placed.
        /// </summary>
        private IEnumerator PlaceBenchAtHero()
        {
            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                _status = "No hero to place it next to.";
                yield break;
            }

            Vector3 at = ArenaSetup.GroundAt(hero.transform.position, out _);
            yield return BenchImporter.PlaceAt(at);

            ArenaSetup.RecordSpawnMarker();
            _status = $"Bench at ({at.x:0.#}, {at.y:0.#}). F9 -> Set here to make this the spawn too.";
        }

        private void TakeOverRoom()
        {
            GauntletMode.Active = true;
            BattleScene taken = WaveBuilder.AdoptRoomBattle();
            ArenaSetup.StripRoom(includeInactive: true);
            ArenaSetup.SealExits();

            GauntletMode.ForceInRoom();
            StartWatchdog();

            if (taken == null)
            {
                _status = "No BattleScene here - this room can't run waves.";
            }
            else if (WaveConfig.IsEmpty)
            {
                WaveBuilder.Disarm();
                _status = $"Took over {WaveBuilder.AdoptedCount} battle(s). Add waves in the editor (F7).";
            }
            else
            {
                WaveBuilder.Rebuild();
                _status = $"Took over {WaveBuilder.AdoptedCount} battle(s) and applied your waves.";
            }
        }

        private void CaptureCurrentScene()
        {
            string scene = GameManager.instance != null
                ? GameManager.instance.sceneName
                : SceneManager.GetActiveScene().name;

            GauntletConfig.GauntletScene.Value = scene;
            _sceneField = scene;
            _status = $"'{scene}' is now the gauntlet room.";
            _detail = "Loaded: " + ArenaSetup.DescribeLoadedScenes();
            Plugin.Log.LogInfo($"Custom Gauntlet: captured gauntlet scene '{scene}' (loaded: {ArenaSetup.DescribeLoadedScenes()}).");
        }

    }
}
