#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Greeny.EditorTools
{
    /// <summary>Commands every project gets. Game-specific ones (noclip, give, ...) live in the game.</summary>
    internal static class DevConsoleBuiltins
    {
        [DevCommand("help", "List all commands, or show how to use one.", Suggestions = nameof(CommandNames))]
        private static string Help(string p_command = null)
        {
            if (!string.IsNullOrWhiteSpace(p_command))
            {
                return DevCommandRegistry.TryGet(p_command.Trim(), out DevCommand command)
                    ? $"{command.Usage}\n    {command.Description}"
                    : $"Unknown command '{p_command}'.";
            }

            List<DevCommand> commands = DevCommandRegistry.All.ToList();
            int width = commands.Max(c => c.Name.Length);

            var sb = new StringBuilder("Commands (help <command> for usage):");
            foreach (DevCommand command in commands)
                sb.Append('\n').Append("  ").Append(command.Name.PadRight(width + 3)).Append(command.Description);
            return sb.ToString();
        }

        private static IEnumerable<string> CommandNames() => DevCommandRegistry.All.Select(c => c.Name);

        [DevCommand("clear", "Clear the console output.")]
        private static void Clear() => DevConsole.Clear();

        private enum HistoryAction { list, clear }

        [DevCommand("history", "List the commands you ran (newest last), or forget them with 'history clear'.")]
        private static string History(HistoryAction p_action = HistoryAction.list, int p_count = 30)
        {
            if (p_action == HistoryAction.clear)
            {
                DevConsole.ClearHistory();
                return "History cleared.";
            }

            IReadOnlyList<string> history = DevConsole.History;
            if (history.Count == 0) return "History is empty.";

            int first = Mathf.Max(0, history.Count - Mathf.Max(1, p_count));
            var sb = new StringBuilder($"History ({history.Count}, Up/Down in the input to recall):");
            for (int i = first; i < history.Count; i++)
                sb.Append('\n').Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ").Append(history[i]);
            return sb.ToString();
        }

        [DevCommand("timescale", "Show or set Time.timeScale (0 freezes, 1 is normal).", Usage = "timescale [scale:number]")]
        private static string TimeScale(float p_scale = -1f)
        {
            if (p_scale >= 0f) Time.timeScale = p_scale;
            return "timeScale = " + Time.timeScale.ToString(CultureInfo.InvariantCulture);
        }
    }
}
#endif
