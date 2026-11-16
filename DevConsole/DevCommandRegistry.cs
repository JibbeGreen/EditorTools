#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Greeny.EditorTools
{
    /// <summary>A command the console can run: a <see cref="DevCommandAttribute"/> method or one added at runtime.</summary>
    public sealed class DevCommand
    {
        public string Name { get; }
        public string Description { get; }
        public string Usage { get; }

        private readonly MethodInfo _method;
        private readonly ParameterInfo[] _parameters;
        private readonly Action<string[]> _rawHandler;
        private readonly Func<IEnumerable<string>> _suggestions;

        internal DevCommand(string p_name, string p_description, MethodInfo p_method, Func<IEnumerable<string>> p_suggestions, string p_usage = null)
        {
            Name = p_name;
            Description = p_description ?? string.Empty;
            _method = p_method;
            _parameters = p_method.GetParameters();
            _suggestions = p_suggestions;
            Usage = string.IsNullOrWhiteSpace(p_usage) ? BuildUsage() : p_usage;
        }

        internal DevCommand(string p_name, string p_description, Action<string[]> p_handler, Func<IEnumerable<string>> p_suggestions)
        {
            Name = p_name;
            Description = p_description ?? string.Empty;
            _rawHandler = p_handler;
            _suggestions = p_suggestions;
            Usage = $"{p_name} [args...]";
        }

        /// <summary>Values for the first argument (Tab completion), or empty.</summary>
        public IEnumerable<string> GetSuggestions()
        {
            if (_suggestions == null) return Enumerable.Empty<string>();
            try { return _suggestions() ?? Enumerable.Empty<string>(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                return Enumerable.Empty<string>();
            }
        }

        public bool HasSuggestions => _suggestions != null;

        /// <summary>Runs the command with the already tokenized arguments (command name excluded).</summary>
        internal void Execute(IReadOnlyList<string> p_args)
        {
            if (_rawHandler != null)
            {
                _rawHandler(p_args.ToArray());
                return;
            }

            if (!TryBind(p_args, out object[] values, out string error))
            {
                DevConsole.LogError(error);
                DevConsole.Log($"Usage: {Usage}");
                return;
            }

            object result;
            try
            {
                result = _method.Invoke(null, values);
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                DevConsole.LogError($"{Name} failed: {inner.Message}");
                Debug.LogException(inner);
                return;
            }

            if (result != null) DevConsole.Log(result.ToString());
        }

        #region ARGUMENT BINDING
        private bool TryBind(IReadOnlyList<string> p_args, out object[] p_values, out string p_error)
        {
            p_values = new object[_parameters.Length];
            p_error = null;

            for (int i = 0; i < _parameters.Length; i++)
            {
                ParameterInfo parameter = _parameters[i];
                bool isLast = i == _parameters.Length - 1;

                // string[] at the end takes everything that's left.
                if (isLast && parameter.ParameterType == typeof(string[]))
                {
                    p_values[i] = p_args.Skip(i).ToArray();
                    return true;
                }

                if (i >= p_args.Count)
                {
                    if (parameter.HasDefaultValue)
                    {
                        p_values[i] = parameter.DefaultValue;
                        continue;
                    }

                    p_error = $"Missing argument '{CleanName(parameter)}'.";
                    return false;
                }

                // A trailing string takes the rest of the line, so "say hello there" needs no quotes.
                string raw = isLast && parameter.ParameterType == typeof(string) && p_args.Count > _parameters.Length
                    ? string.Join(" ", p_args.Skip(i))
                    : p_args[i];

                if (!TryConvert(raw, parameter.ParameterType, out p_values[i]))
                {
                    p_error = $"'{raw}' is not a valid {FriendlyType(parameter.ParameterType)} for '{CleanName(parameter)}'.";
                    return false;
                }
            }

            if (p_args.Count > _parameters.Length && (_parameters.Length == 0 || _parameters[^1].ParameterType != typeof(string)))
            {
                p_error = "Too many arguments.";
                return false;
            }

            return true;
        }

        private static bool TryConvert(string p_raw, Type p_type, out object p_value)
        {
            p_value = null;
            CultureInfo inv = CultureInfo.InvariantCulture;

            if (p_type == typeof(string)) { p_value = p_raw; return true; }
            if (p_type == typeof(int) && int.TryParse(p_raw, NumberStyles.Integer, inv, out int i)) { p_value = i; return true; }
            if (p_type == typeof(float) && float.TryParse(p_raw, NumberStyles.Float, inv, out float f)) { p_value = f; return true; }
            if (p_type == typeof(double) && double.TryParse(p_raw, NumberStyles.Float, inv, out double d)) { p_value = d; return true; }
            if (p_type == typeof(bool))
            {
                switch (p_raw.ToLowerInvariant())
                {
                    case "1": case "true": case "on": case "yes": p_value = true; return true;
                    case "0": case "false": case "off": case "no": p_value = false; return true;
                }
                return false;
            }
            if (p_type.IsEnum && Enum.TryParse(p_type, p_raw, true, out object e) && Enum.IsDefined(p_type, e))
            {
                p_value = e;
                return true;
            }

            return false;
        }

        private string BuildUsage()
        {
            var sb = new StringBuilder(Name);
            foreach (ParameterInfo parameter in _parameters)
            {
                string label = $"{CleanName(parameter)}:{FriendlyType(parameter.ParameterType)}";
                if (parameter.ParameterType == typeof(string[])) sb.Append($" [{CleanName(parameter)}...]");
                else if (parameter.HasDefaultValue)
                {
                    object fallback = parameter.DefaultValue;
                    bool showDefault = fallback != null && !(fallback is string s && s.Length == 0);
                    sb.Append(showDefault ? $" [{label}={Convert.ToString(fallback, CultureInfo.InvariantCulture)}]" : $" [{label}]");
                }
                else sb.Append($" <{label}>");
            }
            return sb.ToString();
        }

        // Strips the p_ / _ prefixes used in this codebase so usage reads "give <item> [amount]".
        private static string CleanName(ParameterInfo p_parameter)
        {
            string name = p_parameter.Name ?? "arg";
            if (name.StartsWith("p_")) name = name.Substring(2);
            return name.TrimStart('_');
        }

        private static string FriendlyType(Type p_type)
        {
            if (p_type == typeof(int)) return "int";
            if (p_type == typeof(float) || p_type == typeof(double)) return "number";
            if (p_type == typeof(bool)) return "bool";
            if (p_type.IsEnum) return string.Join("|", Enum.GetNames(p_type));
            return "text";
        }
        #endregion
    }

    /// <summary>Finds every <see cref="DevCommandAttribute"/> method in the project (via TypeCache) and keeps runtime additions.</summary>
    public static class DevCommandRegistry
    {
        private static Dictionary<string, DevCommand> _commands;

        public static IEnumerable<DevCommand> All
        {
            get
            {
                EnsureBuilt();
                return _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);
            }
        }

        public static bool TryGet(string p_name, out DevCommand p_command)
        {
            EnsureBuilt();
            return _commands.TryGetValue(p_name ?? string.Empty, out p_command);
        }

        /// <summary>Adds a command from code (e.g. a lambda that needs scene state). Replaces one with the same name.</summary>
        public static void Register(string p_name, string p_description, Action<string[]> p_handler, Func<IEnumerable<string>> p_suggestions = null)
        {
            EnsureBuilt();
            if (string.IsNullOrWhiteSpace(p_name) || p_handler == null) return;
            _commands[p_name.Trim()] = new DevCommand(p_name.Trim(), p_description, p_handler, p_suggestions);
        }

        public static void Unregister(string p_name)
        {
            EnsureBuilt();
            if (p_name != null) _commands.Remove(p_name);
        }

        private static void EnsureBuilt()
        {
            if (_commands != null) return;

            _commands = new Dictionary<string, DevCommand>(StringComparer.OrdinalIgnoreCase);

            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<DevCommandAttribute>())
            {
                if (!method.IsStatic)
                {
                    Debug.LogWarning($"[DevConsole] {method.DeclaringType?.Name}.{method.Name} has [DevCommand] but isn't static; ignored.");
                    continue;
                }

                foreach (DevCommandAttribute attribute in method.GetCustomAttributes<DevCommandAttribute>())
                {
                    string name = attribute.Name?.Trim();
                    if (string.IsNullOrEmpty(name) || name.Contains(' '))
                    {
                        Debug.LogWarning($"[DevConsole] {method.DeclaringType?.Name}.{method.Name}: invalid command name '{attribute.Name}'.");
                        continue;
                    }

                    if (_commands.TryGetValue(name, out DevCommand existing))
                    {
                        Debug.LogWarning($"[DevConsole] Command '{name}' is declared twice; keeping the first ({existing.Usage}).");
                        continue;
                    }

                    _commands[name] = new DevCommand(name, attribute.Description, method, ResolveSuggestions(method, attribute), attribute.Usage);
                }
            }
        }

        private static Func<IEnumerable<string>> ResolveSuggestions(MethodInfo p_command, DevCommandAttribute p_attribute)
        {
            if (string.IsNullOrEmpty(p_attribute.Suggestions)) return null;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo provider = p_command.DeclaringType?.GetMethod(p_attribute.Suggestions, flags, null, Type.EmptyTypes, null);

            if (provider == null || !typeof(IEnumerable<string>).IsAssignableFrom(provider.ReturnType))
            {
                Debug.LogWarning($"[DevConsole] '{p_attribute.Name}': suggestions method '{p_attribute.Suggestions}' must be a " +
                                 $"parameterless static method on {p_command.DeclaringType?.Name} returning IEnumerable<string>.");
                return null;
            }

            return () => (IEnumerable<string>)provider.Invoke(null, null);
        }

        #region PARSING
        /// <summary>Splits a line on spaces, keeping "quoted text" together.</summary>
        public static List<string> Tokenize(string p_line)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(p_line)) return tokens;

            var current = new StringBuilder();
            bool inQuotes = false;
            bool hasToken = false;

            foreach (char c in p_line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true; // "" is a valid empty argument
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (hasToken) tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }

            if (hasToken) tokens.Add(current.ToString());
            return tokens;
        }

        /// <summary>Parses and runs one line. Returns false when the command doesn't exist.</summary>
        public static bool Execute(string p_line)
        {
            List<string> tokens = Tokenize(p_line);
            if (tokens.Count == 0) return true;

            if (!TryGet(tokens[0], out DevCommand command))
            {
                DevConsole.LogError($"Unknown command '{tokens[0]}'. Type 'help' for a list.");
                return false;
            }

            tokens.RemoveAt(0);
            try
            {
                command.Execute(tokens);
            }
            catch (Exception e)
            {
                DevConsole.LogError($"{command.Name} failed: {e.Message}");
                Debug.LogException(e);
            }
            return true;
        }
        #endregion
    }
}
#endif
