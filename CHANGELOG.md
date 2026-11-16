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

