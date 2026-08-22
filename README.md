# Custom Gauntlet

A Silksong mod that adds a **Custom Gauntlet** game mode. Picking it from the New Game
menu skips the opening cutscene and drops you into the Grand Forum with every ability
and tool unlocked, a bench to rest at, and an arena that runs waves you built yourself.

## Build

```
dotnet build -c Release
```

Builds both halves and copies each into place:

```
GauntletMod.csproj    plugin  -> BepInEx/plugins/GauntletMod/GauntletMod.dll
Patcher/              patcher -> BepInEx/patchers/GauntletMod/GauntletMod.Patcher.dll
```

Both `<GamePath>` lines — root csproj and `Patcher/GauntletMod.Patcher.csproj` — need to
match if the game ever moves.

## Controls

| key | |
| --- | --- |
| `F7` | Wave editor |
| `F9` | Dev menu (Play / Room / Waves / Debug tabs, draggable, resizable) |
| `1` | Free move — fly with arrows or WASD, shift for speed |
| `2` | Masks/silk readout |
| `-` / `=` | Camera zoom out / in |

## Files

| File | Job |
| --- | --- |
| `Plugin.cs` | BepInEx entry point, applies the Harmony patches |
| `GauntletConfig.cs` | every tunable |
| `GauntletPatches.cs` | the Harmony hooks |
| `MenuInjector.cs` | adds the Custom Gauntlet button to the play-mode menu |
| `GauntletMode.cs` | the mode's state and start sequence |
| `GauntletRun.cs` | one run: timing, reset, tool freedom between fights |
| `ArenaSetup.cs` | clearing, sealing, spawn points, HUD, room diagnostics |
| `ArenaTrigger.cs` | the mod's own "walk in and it starts" trigger |
| `EnemyLibrary.cs` | where spawnable enemies come from |
| `EnemyImporter.cs` | fetches enemies from other rooms |
| `WaveConfig.cs` | the wave list and its file |
| `WaveBuilder.cs` | turns that list into an actual fight |
| `GauntletPanel.cs` | the wave editor window |
| `GauntletHud.cs` | masks/silk readout, end-of-run card |
| `BenchImporter.cs` | lifts a working bench into a room that hasn't got one |
| `PlayerBuffs.cs` | the max-out-everything loadout |
| `FreeMove.cs` | noclip |
| `DebugOverlay.cs` | camera zoom |
| `SceneCatalog.cs` | checks a scene exists before loading it can hang the game |
| `GauntletManager.cs` | scene watching, hotkeys, dev menu, watchdog |

---

# How it works

Everything below is a decision that cost something to learn. It's written down so the
reasoning survives, not just the result.

## Starting without the cutscene

`GameManager.StartNewGame(permadeath, bossRush)` already has two branches:

- `bossRush == false` → `RunStartNewGame()` → loads `Opening_Sequence`, the intro
- `bossRush == true` → `RunContinueGame()` → loads `playerData.respawnScene` directly

The gauntlet takes the second. It isn't a skip bolted on top — it's the cutscene-free
path the game already ships, including the correct global-pool, hero-prefab and
scene-ref setup.

Stock Silksong only shows the play-mode menu if Steel Soul or Boss Rush is unlocked, so
a prefix on `UIManager.StartNewGame` always routes you there, and `MenuInjector` clones
the existing "Normal" button rather than building one — inheriting the menu's fonts,
animators, flash effect, audio and navigation for free.

## The room

`Hang_04` is the Grand Forum, and it's **two scenes at once**: `Hang_04` holds the room,
`Hang_04_boss` is loaded additively on top carrying the `BattleScene`s and waves. An
early version filtered objects by "is this the active scene", which excluded the entire
arena. The rule is inverted now — everything loaded counts as the room unless it's
persistent or a room we imported enemies from.

**The home point is pinned to `(53.5, 4.6)`** and you're moved there on every load.
Automatic placement is only ever a guess about a room's shape.

## Which battle the waves go into

A room can hold several `BattleScene`s for different story states. `Hang_04` has an
inactive `Battle Scene Act3` alongside the live one.

Building into an inactive one breaks everything *silently*: `StartBattle` calls
`StartCoroutine`, Unity refuses to run coroutines on inactive objects, so the fight
never starts and the enemies never switch on. They sit there correctly positioned and
permanently invisible. **Being active outweighs having the most waves** when choosing.

All battles in the room are taken over — the Forum has three, and leaving one armed
means the stock fight runs alongside yours.

## Waves

The mod does **not** implement a wave system. `BattleScene` already locks the camera,
closes the gates, handles music cues, counts enemies down through `HealthManager` death
callbacks and advances waves. A `BattleWave` is just a GameObject whose children are the
enemies — so the mod keeps the room's own `BattleScene` and swaps out its `waves` list.

Stepping in works because the mod places **its own trigger** across the arena. The
room's original trigger can't be relied on once the stock fight has been cleared out.

Wave one is held back by `FirstWaveDelay` so enemies appear after the opening flourish
rather than during it.

### Where enemies get placed

All spawn in the middle of the arena, on the floor, fanned out. The ground raycast fires
from *just above the floor you're standing on*, not from the ceiling — casting downward
from the top finds the first thing beneath it, which in `Hang_04` is a platform twenty
units up, and enemies spawned there are off-screen.

`Collider2D.bounds` is only meaningful while the collider is **enabled**, and a
`BattleScene` disables its own trigger when a fight starts. Reading it disabled returns
a degenerate box at the origin, which would put every spawn point in the room's corner.

Burrowers that dig in and never resurface are lifted out after a couple of seconds —
watched rather than predicted, so a legitimate underground attack is left alone.

## Where enemies come from

Silksong has **six** addressable prefabs in the entire game: three core managers,
`GlobalPool` and `Hero_Hornet`. No enemies. They exist only inside scenes, two ways:

- **Placed** — sitting in the room as objects. Rare; the gauntlet's wave enemies are
  like this, which is why the arena alone yields ~37 types.
- **Pooled** — far more common. The room carries a `PersonalObjectPool` whose
  `startupPool` lists enemy *prefabs*, instantiated at runtime.

The library reads **both**, and the pool's prefab list is the important one. An earlier
version only searched for `HealthManager` objects, which found nothing in most rooms
because their enemies don't exist until the room runs — and the importer hides rooms on
arrival, guaranteeing they never do. Reading prefabs needs no instances and no running
room.

Templates are cloned into an **inactive** parent, so Unity skips `Awake` and the
template never runs enemy logic or registers with the object pool.

### Importing from other rooms

Load a room, take copies, park it. Two Silksong-specific traps:

**The room must stay loaded.** `UnloadSceneAsync(handle, autoReleaseHandle: false)` does
*not* pin the bundle — that flag only controls whether the handle wrapper is recycled.
Unloading a scene unloads its bundle, destroying the serialised data behind the clones'
sprites *even though they've been moved to a persistent scene*. That was the invisible
enemies. So rooms that yield something stay loaded, offset far away and switched off;
rooms that yield nothing are released.

**Palettes must be blocked, not repaired.** Every room's `SceneColorManager` pushes its
palette into the global colour curves on load, and unloading doesn't undo it. A Harmony
prefix skips any colour manager that isn't the arena's own while an import runs.

> The proper solution is **Silksong.AssetHelper**, which repacks individual scene assets
> into their own bundle so no scene load is needed at all. It isn't installed here. If
> it ever is, `EnemyImporter` is what it replaces — note that its requests go in `Awake`,
> so the wave file would become a startup manifest, and it needs a `(scene, path)`
> mapping this mod doesn't have.

## Respawning

`GameManager.FindEntryPoint` has three answers depending on how you arrived: a respawn
marker after a normal death, `playerData.hazardRespawnLocation` after a pit or spike, or
a transition point otherwise. Patching only the marker lookup covers one branch and
misses the others — and `hazardRespawnLocation` is `(0,0,0)` on a fresh save, the
bottom-left corner of the room.

Worse, the bounds check meant to catch that read `x >= 0 && y >= 0`, which calls `(0,0)`
**in bounds**. It was passing the most common bad respawn there is. There's a 3-unit
margin now.

Rather than keep chasing respawn routes, the mod watches for the death flag clearing and
puts you on the configured coordinates — repeatedly for about a second, because the
respawn sequence keeps repositioning you after the flag clears.

Dying also **resets the run to wave one**. The `BattleScene` keeps its wave counter
otherwise, so walking back in resumed mid-run.

## The loadout

Tools aren't `PlayerData` bools — they're `ToolItem` ScriptableObjects tracked through
`ToolItemManager`, so no amount of `SetBool` grants one. `UnlockAllTools()` and
`UnlockAllCrests()` do it, plus `UnlockCrestSockets()` for the sockets that normally cost
a Memory Locket (per-crest save data; `UnlockAllCrests` doesn't touch them).

`maxHealth` is **derived** — `CharmUpdate` does literally `maxHealth = maxHealthBase` —
so setting it directly is pointless. Set the base, then walk the game's own path
(`CharmUpdate` → `MaxHealth` → `HEALTH UPDATE`), which is what makes the HUD rebuild its
mask row.

`ToolItemManager` is a `ManagerSingleton` that doesn't exist on the menu, which is why
the loadout is applied twice: stats at new-game time, everything again on arrival.

## The HUD

The mask/silk HUD lives on its own camera. `MoveMenuToMainCamera` sets that camera's
`cullingMask` to **0** — active, enabled, reporting itself visible, rendering nothing.
No state check gives it away, and it defeats any other mod's HUD toggle too. The fix is
`MoveMenuToHUDCamera`, which repoints the UI canvases and restores the mask.

Deliberately **not** paired with `EnsureGameMapSpawned` — spawning the inventory map out
of band puts a black bar across the screen.

`2` toggles a mod-drawn masks/silk readout as a fallback.

## The bench

A bench can't be fabricated: `RestBench` only flips a "near a bench" flag, and sitting,
healing, saving and changing tools all live in its "Bench Control" FSM. So one is cloned
out of a donor room.

Donor choice matters. Bellway "bell benches" are shrines with an unlock popup, and a
clone shows the popup rather than a bench — a giant glowing sigil in mid-air.
`Hang_06b` is a plain bench from the same area as the arena, so it matches.

It's sat on the floor by measuring its **renderer bounds**, not its pivot: the pivot can
be metres above the artwork, which buries the whole thing underground.

## Doors

The mod doesn't touch them. `SealExits` defaults **off**. An earlier version rewrote
every door's `targetScene` to test which room was which, which destroyed the room's real
connections for that session. `Hang_04` has exactly two doors, to `Hang_06` and
`Hang_12`.

If the arena ever feels sealed, F9 → Room → **Let me out** releases the camera lock and
gates, which a `BattleScene` holds until its own end sequence runs.

## Scene names

Asking Silksong to load a scene that isn't in the Addressables catalog **doesn't error** —
the load never completes and the game sits on the loading screen forever with nothing in
the log. Every scene name is checked against the catalog first.

## The patcher

A BepInEx 5 preloader patcher doing a targeted publicise of the types the mod reaches
into, on the in-memory Cecil assembly. Nothing on disk is modified.

**You don't strictly need it** — the plugin is built against stock assemblies and uses
reflection. Two rules keep it safe: only listed types are touched, and fields Unity
wasn't already serialising get `NotSerialized` when made public (Unity serialises public
fields by default, so publicising a plain private field would change how scenes
deserialise).

## Config

`BepInEx/config/com.custom.gauntletmod.cfg`. Waves live separately in
`BepInEx/config/GauntletMod.waves.txt`, a plain text format that's fine to hand-edit.

| Section | Key | Default |
| --- | --- | --- |
| Room | `GauntletScene` | `Hang_04` |
| Room | `HomePointSet` / `HomePointX` / `HomePointY` | `true` / `53.5` / `4.6` |
| Room | `AddBench` / `BenchDonorScene` / `BenchYOffset` | `true` / `Hang_06b` / `0` |
| Behaviour | `StripArena` | `true` |
| Behaviour | `SealExits` | `false` |
| Behaviour | `KeepRoomClear` | `true` |
| Behaviour | `RescueOutOfBounds` | `true` |
| Behaviour | `SpawnAtArenaCentre` / `SpawnHeight` | `true` / `1.6` |
| Behaviour | `HarvestEnemies` | `true` |
| Behaviour | `ToolsBetweenFights` | `true` |
| Behaviour | `FirstWaveDelay` | `2.5` |
| Behaviour | `ShowClearMessage` | `false` |
| Mode | `ShowModeMenuOnNewGame` / `MaxLoadoutOnStart` | `true` / `true` |
| Startup | `SkipStartupLogos` | `true` |
| Menu | `ModeName` / `ModeDescription` | `CUSTOM GAUNTLET` / `Trials of your own making.` |
| Keys | `WavePanelKey` `DevMenuKey` `FreeMoveKey` `ReadoutKey` `ZoomOutKey` `ZoomInKey` | `F7` `F9` `1` `2` `-` `=` |
| Bookkeeping | `GauntletProfileIds` | *(auto)* |

`GauntletProfileIds` records which save slots are gauntlet runs. The mode is identified
by slot, not by respawn scene — otherwise continuing a normal playthrough parked in the
same room would re-arm it and strip a real save.

## Known rough edges

- **Choir clappers** sometimes stay under the floor. The unstick pass catches most of
  it; their emerge animation isn't authored for an arena they weren't placed in.
- **Imported rooms cost memory** — each one stays loaded so its bundle keeps its
  enemies' artwork. F9 → Waves shows how many are held.
- **No enemy-to-room index.** "Load missing" works by elimination through a list of 32
  rooms, because journal entries store display names that don't match in-scene object
  names and the scene bundles are compressed. AssetHelper's dump utilities would fix
  this properly.
