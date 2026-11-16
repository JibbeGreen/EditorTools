# Greeny Editor Tools

Editor-only tools by Greeny. Nothing in this package ends up in a build.

| Assembly | Contents |
| --- | --- |
| `Greeny.EditorTools.SceneSwitcher` | Scene dropdown in the main toolbar. Lists the build scenes; other assemblies add sections to it. |
| `Greeny.EditorTools.SceneGroups` | Adds every `SceneGroupConfig` to the dropdown. Only compiled when `com.greeny.scenemanagement` is installed. |
| `SceneSwitcher.PlayTest` | Play mode straight into a scene group or the latest save. Needs `com.greeny.scenemanagement` and `com.greeny.persistence`. |
| `Greeny.EditorTools.DevConsole` | In-game developer console (F1 in play mode). Compiled only in the editor (`UNITY_EDITOR` define constraint). |

## Installation

Add this git repository URL to your Unity project's Packages/manifest.json:

```json
"dependencies": {
    "com.greeny.editortools": "https://github.com/JibbeGreen/EditorTools.git"
}
```

## Scene switcher

The dropdown sits in the middle of the main toolbar and shows the active scene. Right-click the toolbar to move or
hide it (**Greeny/Scene Switcher**).

| Entry | Does |
| --- | --- |
| *Group name* | Opens the scene that contains the `Bootstrapper` component, then the group's scenes. The scene marked `ActiveScene` becomes active. Ticked when the whole group is open |
| Load Bootstrapper On Play | Same toggle as **Scene Loading > Load Bootstrapper On Play**. On: Play starts from the Bootstrapper and goes to the main menu. Off: Play runs the open scenes as they are |
| Scenes > *scene* | Opens the Bootstrapper plus that scene (set as active). Lists the enabled build scenes, then scenes only used by scene groups |

The Bootstrapper is only added when `com.greeny.scenemanagement` is installed; without it, groups and the toggle are
hidden and **Scenes** opens the single scene. Play Test stays in the menu bar (**Play Test/...**) for CLI use.

Groups are the `SceneGroupConfig` assets in the project, labelled with their `GroupName` (the asset name is added when
two groups share a name). Nothing is hardcoded, so the tool works the same in every project.

Add your own entries from an editor assembly:

```csharp
[InitializeOnLoad]
static class MyScenes
{
    static MyScenes() => SceneSwitcher.AddMenuSection(50, menu =>
        menu.AddItem("Tools/Lighting Test", false, SceneSwitcher.CanOpenScenes,
            () => SceneSwitcher.OpenScenes("Assets/Scenes/LightingTest.unity")));
}
```

## Developer console

Press **F1** in play mode. Type a command and press **Enter**.

While you type, a suggestion list pops up above the input for the command name or the argument under
construction, and the hint line below shows the command's usage with the current argument highlighted
(red when an argument is invalid). A grey preview shows the rest of the highlighted suggestion.

| Key | Action |
| --- | --- |
| Enter | Run the command |
| Tab / Shift+Tab | Accept the highlighted suggestion; press again to cycle through the list. A single match adds a space so the next argument's list opens |
| Right arrow (at the end) | Accept the grey preview |
| Up / Down | Move through the suggestion list; when it's closed, browse history. Text typed before browsing filters the history (`sp` + Up = earlier `spawn` commands) |
| Esc | Hide the suggestion list, or close the console when it's already hidden |
| F1 | Close |

History is stored per project in EditorPrefs, so it survives play sessions and editor restarts (newest last,
duplicates moved to the end, 200 entries).

While the console is open the keyboard and mouse are disconnected from the Input System, so typing never
moves the player or fires hotkeys. The cursor is unlocked and restored on close.

Built-in commands: `help [command]`, `clear`, `history [list|clear] [count]`, `timescale [value]`.

### Game commands (`Greeny.EditorTools.DevConsole.Commands`)

| Command | Does |
| --- | --- |
| `heal [amount=full] [target=@p]` | Restore health (`heal`, `heal 25`, `heal @t`, `heal 25 guard`) |
| `kill [target=@p]` | Kill instantly, even when invincible |
| `damage <amount> [target=@p]` | Deal damage (respects invincibility) |
| `clearinventory [target=@p] [item]` (`clearinv`) | Empty an inventory, or remove one item; the item list suggests what the target carries |
| `spawn <character> [count=1]` | Spawn a character prefab (any prefab with an `Actor` on its root) under the crosshair, or 3 m in front of the player |
| `characters [filter]` | List the prefabs `spawn` can create |
| `give <item> [amount]`, `items [filter]`, `noclip` | Items and free camera |

Targets work like chat-command selectors and default to the player (the possessed character):
`@p` player, `@e` everyone, `@o` everyone but the player, `@n` nearest to the player, `@t` under the crosshair,
`@r` someone random, or a name (`guard`; `guard*` hits every match). Names with spaces can be typed without quotes
when the target is the last argument.

Spawned characters get fresh save ids, their prefab GUID (so the save system can respawn them elsewhere) and Reflex
injection from the active scene's container, the same way `DisplacedActors` respawns actors.

### Adding commands

Mark any static method, in any assembly, with `[DevCommand]`. Wrap it in `#if UNITY_EDITOR`, because the
console doesn't exist in builds.

```csharp
#if UNITY_EDITOR
using Greeny.EditorTools;

public static class MyCommands
{
    // bool and enum parameters get suggestions automatically.
    [DevCommand("godmode", "Toggle invincibility.")]
    private static void GodMode(bool p_on) { /* ... */ }

    // Suggestions for one parameter. Returning a value prints it.
    [DevCommand("load", "Load a level by name.")]
    private static string Load([DevSuggestions(nameof(LevelNames))] string p_level) { /* ... */ return $"Loading {p_level}"; }
    private static IEnumerable<string> LevelNames() => new[] { "Level01", "Level02" };

    // Suggestions for any argument, depending on what was typed before it.
    [DevCommand("tp", "Teleport to a marker in a level.", Usage = "tp <level> <marker>", Suggestions = nameof(TpSuggestions))]
    private static void Teleport(string p_level, string p_marker) { /* ... */ }
    private static IEnumerable<string> TpSuggestions(int p_argIndex, IReadOnlyList<string> p_args) =>
        p_argIndex == 0 ? LevelNames() : MarkersIn(p_args[0]);
}
#endif
```

Suggestion sources, checked in this order:

* `[DevCommand(Suggestions = "M")]`: `M()` lists the first argument, `M(int p_argIndex, IReadOnlyList<string> p_args)` any
  argument (`p_args` = the arguments before it). Return `null` to fall through to the parameter's own suggestions.
* `[DevSuggestions("M")]` on a parameter: `M()` or `M(IReadOnlyList<string> p_args)`. On a trailing `string[]` it covers
  every remaining argument.
* `bool` and enum parameters: their values.

Matches that start with the typed text come first, then ones that contain it. Values with spaces are quoted when accepted.

Supported parameter types: `string`, `int`, `float`, `double`, `bool` (`true/false/on/off/1/0`) and enums.
Parameters with default values are optional. A trailing `string` receives the rest of the line; a trailing
`string[]` receives all remaining arguments. Use `"double quotes"` for arguments with spaces.
`Usage` overrides the generated usage line; each space-separated part is highlighted while that argument is typed.

Commands can also be added at runtime with `DevCommandRegistry.Register(name, description, args => ..., suggestions, usage)`,
where `suggestions` is a `Func<IEnumerable<string>>` (first argument) or a `DevSuggestionProvider` (any argument).
Print output with `DevConsole.Log`, `DevConsole.LogWarning` and `DevConsole.LogError`. `DevConsole.History` and
`DevConsole.ClearHistory()` expose the history.

`FreeFlyCamera` is a ready-made fly controller (WASD, E/Q, Shift, Ctrl, mouse, scroll) for noclip-style commands.
