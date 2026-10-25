# MiSide co-op: reverse-engineering notes

Notes kept while building the mod with the
[coop-anything](https://github.com/cranebeaker/coop-anything) playbook. Facts are split into
observations (seen in a run), derived (follows from observations), guesses and unknowns.
Only checks that actually ran are reported. Paths under `local/` are private, git-ignored
state of the dev PC (logs, dumps, test installs).

## Identity

| | |
|---|---|
| Game | MiSide (AIHASTO), Steam AppID 2527500 |
| Build | Steam buildid 19029065, `Application.version` `0.93L` |
| Engine | Unity 2021.3.35f1 (157b46ce122a), IL2CPP, Windows x64, Direct3D 11 |
| `MiSideFull.exe` | sha256 `562723c950c2be95ecbdc516530aee56b97837e2ac6610b8b5bce394e23022c0` |
| `GameAssembly.dll` | sha256 `96346a02014f4e1efad20e7d433b474a8e558fcffcec18b7f15c219ffacf46e5` |
| `global-metadata.dat` | sha256 `d034eb17b4285153b9bd9bed2a43c963b7740d4e502a29caec09661965deb9cb` |
| OS | Windows 11 Pro 10.0.26200 |
| Loader | BepInEx 6.0.0-be.755+3fab71a, Unity.IL2CPP win-x64, zip sha256 `3616d6a67f5f595973ec4aa7bd7edaf7f799d5bb9926f7146a6dcc7b4abf478f` |

Re-check the hashes before trusting these notes on another build.

## How the mod works (short)

- **Session.** Two players, host and guest. Transports: Steam (`ISteamNetworkingMessages`
  through the game's own `steam_api64.dll`, friends find the host through rich presence) and
  direct UDP (LiteNetLib). Both carry the same messages (`src/MiSideCoop/Net/Protocol.cs`).
- **Bodies.** Each side samples its player (root and bones, `Game/PlayerSampler.cs`, `Game/Rig.cs`)
  and shows the other as a puppet: a clone of the local player model with its scripts removed
  (`Game/Puppet.cs`). The name tag is drawn with IMGUI.
- **Level.** The host announces its level and an epoch (one number per level instance). The guest
  loads the same level and holds back its own level changes (`Game/SceneFollower.cs`,
  `GamePatches.GuestHoldsSceneLoad`).
- **Story.** The level's story runs through `UnityEvent`s on the level's scripts. The host logs
  every top-level `UnityEvent.Invoke` of the level under a key that is the same on both
  machines (`<scene>:<object path>|<script>#<n>.<field>`). The guest suppresses its own copies
  of those events and replays the host's in order (`Story/EventMap.cs`, `Story/StorySync.cs`).
  Two input-driven methods are replayed as calls: `Tamagotchi_Dialogue.KeyNextDialogue` and
  `DialogueChanger.ClickButton`.
- **Guest protections.** While replaying, the host's interactions do not grab the guest's
  character (`PlayerMove` animation/item/camera methods are skipped). The guest does not save.
  The guest is brought next to the host on arrival in a level and when the host jumps far
  (`Game/Regroup.cs`).
- **Tests.** An in-game JSON bridge on loopback (`Bridge/`) lets Python scripts drive two
  isolated test copies of the game: `tools/test_pair.py`, `tools/test_story.py`, `tools/test_all.py`.

## Observations

Seen directly in runs on the build above. Source: the run named in each line.

1. BepInEx be.755 loads on this build. The first start downloads the Unity 2021.3.35 base
   libraries (`UnityBaseLibrariesSource = https://unity.bepinex.dev/libraries/{VERSION}.zip` in
   `BepInEx.cfg`, kept as `BepInEx\unity-libs\2021.3.35.zip`) and generates `BepInEx\interop`, so it
   needs internet. On the fresh copy C the plugin was up 15 s after the first start.
   (test installs A, B, C; `BepInEx\LogOutput.log`; `check_c.py` run)
2. Unity derives the save folder and PlayerPrefs from the product name in `globalgamemanagers`
   (`MiSideFull`). Renaming it to `MiSideTstA` / `MiSideTstB` (same length) gives each test copy
   its own `LocalLow\AIHASTO\<product>` and registry key. After all the runs of 2026-10-25 the
   real `LocalLow\AIHASTO\MiSideFull\Save` is byte-identical to the backup taken before them.
   (`tools/isolate_test_install.py`, `diff -rq` against `local/backup/saves-20261025-160100`)
3. Test copies with `steam_appid.txt` (2527500) start from their own folder and still see the
   local Steam session. Not tried: starting a copy without that file.
   (`tools/setup-test-installs.ps1`, `tools/launch.py`)
4. Level order in the build settings: `SceneAihasto`, `SceneLoading`, `SceneMenu`,
   `Scene 1 - RealRoom` … `Scene 20 - FightMita` (two levels are named `Backrooms`: 7 and 11),
   `MinigameShooter`. (bridge eval `scenes.build`)
5. Loading a level with `SceneManager.LoadScene(level)` straight from the main menu gives a
   level without the persistent `GameController` and a stream of NullReferenceExceptions.
   Setting `GlobalGame.LoadingLevel` (in `Assembly-CSharp-firstpass`) and loading
   `SceneLoading` works like the game's own path. From inside a level (a `GameController`
   exists) `LoadScene(level)` works. (scratch runs `exp_loading.py`, `exp_level2.py`, B player log)
6. The game's own level changes go through `Scene_Load.StartLoad` / `GoScene`
   (`modeLoad` Single for a level change, Additive for sub-scenes) and `SceneLoading.LoadGo`.
   (Harmony log hooks in `GamePatches.cs`, BepInEx logs)
7. `TextMesh` is stripped from the IL2CPP build: `AddComponent<TextMesh>` throws
   "does not have a corresponding IL2CPP class pointer". Generic `AddComponent<T>` instances can
   be missing too; the `Type` overloads work. (puppet creation, A/B BepInEx logs)
8. `SteamAPI_IsSteamRunning` returned false while Steam was running and `BLoggedOn` was true, so
   it cannot gate Steam use. The game initialises Steam itself (`SteamManager.Initialized`).
   Interfaces found: `SteamFriends017`, `SteamUser021`, `SteamNetworkingMessages002`,
   `SteamNetworkingUtils003`. (read-only smoke test `smoke_steam.py` on a real Steam account)
9. Level 1 story, played by a bot on the host: every story step fires `UnityEvent`s on level
   objects (buttons, tutorial timers, `ObjectInteractive`, key hints of the phone). With the
   guest replaying them, the active objects of the level (`World/...`) and the phone game's
   dialogue line are equal on both sides after every step.
   (`tools/test_story.py`, run 2026-10-25 17:54: 72 events replayed, 42 suppressed, 0 missing)
10. `Mesh_VisibleEvents` fires its events every frame while a mesh is visible: 3453 events in
    about 20 seconds. (story trace, first level 2 run)
11. `PlayerMove.AnimationPlay`, `AnimationHandPlay(Fast)`, `TakeItem`, `Look` and
    `CameraTargetOther` move or turn the local player for an interaction. Replayed on the guest
    without the skip, they pulled the guest's character into the host's spot (both cameras inside
    the puppets' heads). (scratch run `exp_interact.py`, screenshots `int-host.png`, `int-guest.png`)
12. Level 2 (`Scene 2 - InGame`), first three tasks, played by an exploring bot on the host:
    226 of 226 logged events replayed on the guest; 2 events of the save interface
    (`InterfaceSave`) were not found on the guest. (scratch run `explore.py`)
13. `Scene_Load.SaveGame` and `SilentSave` write the save; skipped on the guest, the guest's
    `Save` folder stays byte-identical over a level. (B save folder compared before and after)
14. In a direct jump to `Scene 3 - WeTogether` the `TriggerForShowMita` zone did not fire for the
    bot: it needs its `objectTarget`, not the player. (scratch run `to_level.py`)

## Derived

1. Story state of a level instance is (start of the level) + (ordered `UnityEvent`s fired in it),
   for the parts tested: replaying the host's events in order on a fresh copy of the level gives
   the same active objects (obs. 9, 12).
2. Object paths (with `#n` for same-named siblings) are stable between two machines on the same
   build, so they can name events without a shared id table (obs. 9: 0 missing).
3. Only top-level events need to be sent: nested ones are fired by the replayed outer event on
   the guest as well. Events that repeat every frame are presentation, not story (obs. 10): the
   mod sends at most the first 3 frames of a streak.
4. The guest must not run its own level changes or saves, or it leaves the host's level or
   writes the host's progress into its own save (obs. 6, 13).

## Guesses

1. Levels 3–20 drive their story the same way (UnityEvents on level objects). Only levels 1 and 2
   were played by a bot.
2. Mita's AI runs on each machine from its local inputs, so on the guest she may look at or walk
   to the guest.
3. Steam P2P between two real accounts works: the transport is the same code as the mock Steam
   test, and the real Steam calls it uses were checked read-only.
4. Saved progress inside a level (continue from a save mid-chapter) is restored partly outside
   UnityEvents, so a guest could see a different world after the host continues mid-chapter.

## Unknown

1. Real two-account Steam networking: connect, latency, relay. Needs a second account or a friend.
2. Steam "Join game" from the friends list (`connect` rich presence → `+coop_join <id>` on the
   command line). Not run.
3. Minigames (phone games beyond the first, `MinigameShooter`, `Scene 18 - 2D`,
   `Scene 20 - FightMita`) under co-op.
4. Whether the guest's own clicks on story objects can leave its character in an animation.
5. How the game restores progress when continuing mid-level.

## What was tried

- Loading the host's level straight with `SceneManager.LoadScene` from the menu: broken level
  (obs. 5). Fixed by the game's loading path.
- A `TextMesh` name tag: stripped (obs. 7). IMGUI label instead. A failing puppet creation also
  leaked a clone every frame: creation now cleans up after itself and retries every 2 s.
- Gating Steam on `SteamAPI_IsSteamRunning`: wrong answer (obs. 8). Removed.
- Replaying a method directly in the test (button handler) skipped the button's own events.
  Bots now click the way the game does (`ButtonMouseClick.PointerDown/Up`, `ObjectInteractive.Click`).
- Sending every event: flooded by per-frame events (obs. 10). Streak rule added.
- Comparing the worlds right after the guest caught up: the guest entered a little later, so its
  local cutscene ended later. The story test now waits up to 15 s for the worlds to settle.
- An apparent drift of the guest's position during a run was a manual move of the player in that window.

## Test summary

`python -I tools/test_all.py`, 2026-10-25, build 19029065, two test copies on one PC:

```
PASS  pair over UDP          puppet error max 0.000 m, rtt host 18 ms
PASS  pair over mock Steam   puppet error max 0.000 m, rtt host 6 ms
PASS  story, level 1         72 events replayed, 42 suppressed, 0 missing, worlds equal
RESULT: PASS (3/3)
```

QA level: automated for what these tests cover (connect, follow, puppets, level 1 story). Real
Steam between two accounts and levels after 2 are not covered.

Installer (`dist/MiSideCoop-0.1.0.zip` from `tools/package.py`), 2026-10-25, on a fresh third
test copy `local/installs/C`:

- `-DryRun` without `-GameDir` finds the real install through the Steam registry key,
  `libraryfolders.vdf` and `appmanifest_2527500.acf`, and the tested `GameAssembly.dll` hash;
  nothing is written.
- Install into C, start C: BepInEx generates its files, the plugin loads, the game reaches the
  main menu (after the first-start language screen) and the F7 menu shows Steam ready.
- Uninstall-all after install: the file list of C equals the list before install (0 differences).
- BepInEx already present with another plugin: only the mod is installed, uninstall-all leaves
  exactly the earlier state. Our BepInEx plus a plugin added later: uninstall-all keeps BepInEx.

## Sources

- Playbook: <https://github.com/cranebeaker/coop-anything>
- BepInEx bleeding-edge builds: <https://builds.bepinex.dev/projects/bepinex_be>
- Il2CppInterop: <https://github.com/BepInEx/Il2CppInterop>
- HarmonyX: <https://github.com/BepInEx/HarmonyX>
- LiteNetLib: <https://github.com/RevenantX/LiteNetLib>
- Steamworks `ISteamNetworkingMessages`: <https://partner.steamgames.com/doc/api/ISteamNetworkingMessages>
- Steamworks rich presence (`connect`, `status`): <https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence>
- Unity `SceneManager.LoadScene`: <https://docs.unity3d.com/2021.3/Documentation/ScriptReference/SceneManagement.SceneManager.LoadScene.html>
- Game type dumps: `tools/typedump` output in `local/dumps/Assembly-CSharp*.txt` (not committed:
  derived from game files)
- Runs: `local/logs/pair-*`, `local/logs/story-*`, `local/logs/test_all-latest.txt` (dev PC, not committed)
