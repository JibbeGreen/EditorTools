## \[3.0.0] - 2026-11-16

### Scene switcher

* The project-specific **Switch Scene** menu is gone. The switcher no longer contains any scene paths.
* New scene dropdown in the main toolbar (Unity 6.3+), showing the active scene. It lists:
  * every `SceneGroupConfig` in the project (when `com.greeny.scenemanagement` is installed). Opening a group opens the
    scene that contains the `Bootstrapper` (found by component, not build order) plus the group's scenes, with the group's `ActiveScene` set as active.
  * **Load Bootstrapper On Play** toggle (same EditorPref as the Scene Loading menu).
  * **Scenes**: the enabled build scenes, plus scenes only used by scene groups. Opens the Bootstrapper plus that scene.
* Groups are found each time the dropdown opens, so new or renamed groups need no code change or recompile.
* `SceneSwitcher.AddMenuSection` / `AddSceneSource` let other editor assemblies add their own entries.
* Requires `com.greeny.scenemanagement` 1.0.4 or newer for the scene group and Play Test parts.
* `SceneSwitcher.OpenScenes(paths, activeScenePath)` overload, `OpenWithBootstrapper`, `FirstEnabledBuildScenePath`
  and the `BootstrapperScenePath` hook (set by the scene group switcher).
* New optional assembly `Greeny.EditorTools.SceneGroups` (`SceneGroupSwitcher.FindGroups()`, `Open(config)`).

### Play Test

* Removed the hardcoded `Play Test/Level01 (Scene State)` and `Level02` entries. Use `Play Test/Selected Scene Group`
  with a `SceneGroupConfig` selected (also works with `invoke_menu` from the CLI), or `PlayTest.Play("SG_Name")`.

### Breaking

* `SceneSwitcher.LoadMainMenu`, `LoadLevel01`, `LoadLevel02`, `LoadBootstrapperScene` and `LoadUIScene` are removed.
* `SceneSwitcher.OpenScenes` now returns `bool`.

## \[2.1.0] - 2026-11-16

### Developer console

* Suggestion popup above the input for the command name and every argument, with a grey preview of the highlighted entry.
  Tab / Shift+Tab accept and cycle, Up/Down pick, Right arrow accepts the preview, Esc hides the list, mouse click/scroll work.
* Hint line shows the usage with the current argument highlighted, and the first invalid argument in red.
* Per-argument suggestions: `[DevSuggestions]` on parameters, `Suggestions` methods taking `(int, IReadOnlyList<string>)`,
  automatic values for bool and enum parameters, `DevSuggestionProvider` overload of `DevCommandRegistry.Register`.
* History is saved per project in EditorPrefs (survives editor restarts), deduplicated, and filtered by the typed prefix.
  New `history [list|clear] [count]` command, `DevConsole.History` and `DevConsole.ClearHistory()`.
* Game commands: `heal`, `kill`, `damage`, `clearinventory` / `clearinv` with `@p @e @o @n @t @r` / name targets
  (player by default), `spawn <character> [count]` and `characters`.

## \[2.0.0] - 2026-11-16

### Renamed to Greeny Editor Tools

* Package renamed from `com.greeny.sceneswitcher` to `com.greeny.editortools`. Update the key in Packages/manifest.json.
* SceneSwitcher assembly renamed to `Greeny.EditorTools.SceneSwitcher`.

### Added

* In-game developer console (`Greeny.EditorTools.DevConsole`, editor only): F1 in play mode, `[DevCommand]` attribute,
  history, Tab completion, built-in `help`, `clear` and `timescale` commands.
* `FreeFlyCamera` component for noclip-style commands.

## \[1.0.0] - 2026-09-06

### First Release

* Includes sceneswitcher tool logic.

