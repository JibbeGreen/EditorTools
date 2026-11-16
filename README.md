# Greeny Editor Tools

Editor-only tools by Greeny. Nothing in this package ends up in a build.

| Assembly | Contents |
| --- | --- |
| `Greeny.EditorTools.SceneSwitcher` | **Switch Scene** menu that opens the Bootstrapper + a level in one click. |
| `Greeny.EditorTools.DevConsole` | In-game developer console (F1 in play mode). Compiled only in the editor (`UNITY_EDITOR` define constraint). |

## Installation

Add this git repository URL to your Unity project's Packages/manifest.json:

```json
"dependencies": {
    "com.greeny.editortools": "https://github.com/JibbeGreen/EditorTools.git"
}
```

## Developer console

Press **F1** in play mode. Type a command and press **Enter**.

| Key | Action |
| --- | --- |
| Enter | Run the command |
| Up / Down | Command history (kept between play sessions) |
| Tab | Complete the command name, or its first argument when the command provides suggestions |
| Esc / F1 | Close |

While the console is open the keyboard and mouse are disconnected from the Input System, so typing never
moves the player or fires hotkeys. The cursor is unlocked and restored on close.

Built-in commands: `help [command]`, `clear`, `timescale [value]`.

### Adding commands

Mark any static method, in any assembly, with `[DevCommand]`. Wrap it in `#if UNITY_EDITOR`, because the
console doesn't exist in builds.

```csharp
#if UNITY_EDITOR
using Greeny.EditorTools;

public static class MyCommands
{
    [DevCommand("heal", "Restore the player's health.")]
    private static void Heal(float p_amount = 100f) { /* ... */ }

    // Returning a value prints it. Suggestions feed Tab completion for the first argument.
    [DevCommand("load", "Load a level by name.", Suggestions = nameof(LevelNames))]
    private static string Load(string p_level) { /* ... */ return $"Loading {p_level}"; }
    private static IEnumerable<string> LevelNames() => new[] { "Level01", "Level02" };
}
#endif
```

Supported parameter types: `string`, `int`, `float`, `double`, `bool` (`true/false/on/off/1/0`) and enums.
Parameters with default values are optional. A trailing `string` receives the rest of the line; a trailing
`string[]` receives all remaining arguments. Use `"double quotes"` for arguments with spaces.

Commands can also be added at runtime with `DevCommandRegistry.Register(name, description, args => ...)`.
Print output with `DevConsole.Log`, `DevConsole.LogWarning` and `DevConsole.LogError`.

`FreeFlyCamera` is a ready-made fly controller (WASD, E/Q, Shift, Ctrl, mouse, scroll) for noclip-style commands.
