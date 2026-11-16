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

        /// <summary>The argument parts of <see cref="Usage"/> ("&lt;item&gt;", "[amount]"), used to highlight the one being typed.</summary>
        public IReadOnlyList<string> UsageArguments { get; }

        private readonly MethodInfo _method;
        private readonly ParameterInfo[] _parameters;
        private readonly Action<string[]> _rawHandler;
        private readonly DevSuggestionProvider _suggestions;          // command level, any argument
        private readonly DevSuggestionProvider[] _parameterSuggestions; // per parameter (attribute, bool, enum)

        internal DevCommand(string p_name, string p_description, MethodInfo p_method, DevSuggestionProvider p_suggestions,
                            DevSuggestionProvider[] p_parameterSuggestions, string p_usage = null)
        {
            Name = p_name;
            Description = p_description ?? string.Empty;
            _method = p_method;
            _parameters = p_method.GetParameters();
            _suggestions = p_suggestions;
            _parameterSuggestions = p_parameterSuggestions;
            Usage = string.IsNullOrWhiteSpace(p_usage) ? BuildUsage() : p_usage;
            UsageArguments = SplitUsage(Usage);
        }

        internal DevCommand(string p_name, string p_description, Action<string[]> p_handler, DevSuggestionProvider p_suggestions, string p_usage = null)
        {
            Name = p_name;
            Description = p_description ?? string.Empty;
            _rawHandler = p_handler;
            _suggestions = p_suggestions;
            _parameters = Array.Empty<ParameterInfo>();
            Usage = string.IsNullOrWhiteSpace(p_usage) ? $"{p_name} [args...]" : p_usage;
            UsageArguments = SplitUsage(Usage);
        }

        #region SUGGESTIONS
        public bool HasSuggestions => _suggestions != null || (_parameterSuggestions?.Any(s => s != null) ?? false);

        /// <summary>Values for the first argument, or empty.</summary>
        public IEnumerable<string> GetSuggestions() => GetSuggestions(0, Array.Empty<string>());

        /// <summary>Values for argument <paramref name="p_argIndex"/>; <paramref name="p_args"/> are the arguments typed before it.</summary>
        public IEnumerable<string> GetSuggestions(int p_argIndex, IReadOnlyList<string> p_args)
        {
            IEnumerable<string> result = Invoke(_suggestions, p_argIndex, p_args);
            if (result != null) return result;

            int parameter = ParameterIndexFor(p_argIndex);
            if (parameter < 0 || _parameterSuggestions == null) return Enumerable.Empty<string>();
            return Invoke(_parameterSuggestions[parameter], p_argIndex, p_args) ?? Enumerable.Empty<string>();
        }

        private static IEnumerable<string> Invoke(DevSuggestionProvider p_provider, int p_argIndex, IReadOnlyList<string> p_args)
        {
            if (p_provider == null) return null;
            try
            {
                // Materialised here so a provider that throws while enumerating is caught too.
                return p_provider(p_argIndex, p_args)?.Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>Index into <see cref="UsageArguments"/> for argument <paramref name="p_argIndex"/>, or -1 past the end.</summary>
        public int UsageIndexFor(int p_argIndex)
        {
            if (UsageArguments.Count == 0 || p_argIndex < 0) return -1;
            if (p_argIndex < UsageArguments.Count) return p_argIndex;

            string last = UsageArguments[^1];
            bool repeats = last.EndsWith("...]") || last.EndsWith("...>");
            bool restOfLine = _method != null && _parameters.Length > 0 && _parameters[^1].ParameterType == typeof(string);
            return repeats || restOfLine ? UsageArguments.Count - 1 : -1;
        }

        /// <summary>Checks one typed argument against its parameter type. Raw (runtime) commands accept everything.</summary>
        public bool ValidateArgument(int p_argIndex, string p_raw, out string p_error)
        {
            p_error = null;
            if (_method == null) return true;

            int index = ParameterIndexFor(p_argIndex);
            if (index < 0)
            {
                p_error = "Too many arguments.";
                return false;
            }

            Type type = _parameters[index].ParameterType;
            if (type == typeof(string) || type == typeof(string[]) || TryConvert(p_raw, type, out _)) return true;

            p_error = $"'{p_raw}' is not a valid {FriendlyType(type)} for '{CleanName(_parameters[index])}'.";
            return false;
        }

        // Which parameter receives argument p_argIndex: trailing string / string[] parameters take the rest.
        private int ParameterIndexFor(int p_argIndex)
        {
            if (_parameters.Length == 0 || p_argIndex < 0) return -1;
            if (p_argIndex < _parameters.Length) return p_argIndex;

            Type last = _parameters[^1].ParameterType;
            return last == typeof(string) || last == typeof(string[]) ? _parameters.Length - 1 : -1;
        }
        #endregion

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

        // "give <item> [amount:int=1]" -> "<item>", "[amount:int=1]". Spaces inside <> or [] don't split.
        private static IReadOnlyList<string> SplitUsage(string p_usage)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;

            foreach (char c in p_usage ?? string.Empty)
            {
                if (c is '<' or '[') depth++;
                else if (c is '>' or ']') depth = Mathf.Max(0, depth - 1);

                if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (current.Length > 0) parts.Add(current.ToString());
                    current.Clear();
                }
                else current.Append(c);
            }
            if (current.Length > 0) parts.Add(current.ToString());

            if (parts.Count > 0) parts.RemoveAt(0); // the command name
            return parts;
        }

        // Strips the p_ / _ prefixes used in this codebase so usage reads "give <item> [amount]".
        internal static string CleanName(ParameterInfo p_parameter)
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

        private static readonly string[] BoolValues = { "true", "false" };

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
        /// <param name="p_suggestions">Values for the first argument.</param>
        public static void Register(string p_name, string p_description, Action<string[]> p_handler, Func<IEnumerable<string>> p_suggestions = null)
        {
            DevSuggestionProvider provider = null;
            if (p_suggestions != null) provider = (p_index, _) => p_index == 0 ? p_suggestions() : null;
            Register(p_name, p_description, p_handler, provider);
        }

        /// <summary>Adds a command from code with suggestions for any argument and an optional usage line.</summary>
        public static void Register(string p_name, string p_description, Action<string[]> p_handler, DevSuggestionProvider p_suggestions, string p_usage = null)
        {
            EnsureBuilt();
            if (string.IsNullOrWhiteSpace(p_name) || p_handler == null) return;
            _commands[p_name.Trim()] = new DevCommand(p_name.Trim(), p_description, p_handler, p_suggestions, p_usage);
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

                DevSuggestionProvider[] parameterSuggestions = method.GetParameters().Select(p => ResolveParameterSuggestions(method, p)).ToArray();

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

                    _commands[name] = new DevCommand(name, attribute.Description, method, ResolveCommandSuggestions(method, attribute),
                                                     parameterSuggestions, attribute.Usage);
                }
            }
        }

        #region SUGGESTION PROVIDERS
        private const BindingFlags ProviderFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // [DevCommand(Suggestions = "M")]: M() for the first argument, or M(int, IReadOnlyList<string>) for any.
        private static DevSuggestionProvider ResolveCommandSuggestions(MethodInfo p_command, DevCommandAttribute p_attribute)
        {
            if (string.IsNullOrEmpty(p_attribute.Suggestions)) return null;

            foreach (MethodInfo provider in FindProviders(p_command, p_attribute.Suggestions))
            {
                ParameterInfo[] parameters = provider.GetParameters();
                if (parameters.Length == 0)
                    return (p_index, _) => p_index == 0 ? (IEnumerable<string>)provider.Invoke(null, null) : null;

                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int) &&
                    parameters[1].ParameterType.IsAssignableFrom(typeof(string[])))
                    return (p_index, p_args) => (IEnumerable<string>)provider.Invoke(null, new object[] { p_index, p_args.ToArray() });
            }

            Debug.LogWarning($"[DevConsole] '{p_attribute.Name}': suggestions method '{p_attribute.Suggestions}' must be a static method on " +
                             $"{p_command.DeclaringType?.Name} returning IEnumerable<string>, taking () or (int, IReadOnlyList<string>).");
            return null;
        }

        // [DevSuggestions("M")] on a parameter: M() or M(IReadOnlyList<string>). Otherwise bool / enum values.
        private static DevSuggestionProvider ResolveParameterSuggestions(MethodInfo p_command, ParameterInfo p_parameter)
        {
            DevSuggestionsAttribute attribute = p_parameter.GetCustomAttribute<DevSuggestionsAttribute>();
            if (attribute != null && !string.IsNullOrEmpty(attribute.Method))
            {
                foreach (MethodInfo provider in FindProviders(p_command, attribute.Method))
                {
                    ParameterInfo[] parameters = provider.GetParameters();
                    if (parameters.Length == 0)
                        return (_, _) => (IEnumerable<string>)provider.Invoke(null, null);

                    if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(string[])))
                        return (_, p_args) => (IEnumerable<string>)provider.Invoke(null, new object[] { p_args.ToArray() });
                }

                Debug.LogWarning($"[DevConsole] {p_command.DeclaringType?.Name}.{p_command.Name}({p_parameter.Name}): suggestions method " +
                                 $"'{attribute.Method}' must be static, return IEnumerable<string> and take () or (IReadOnlyList<string>).");
            }

            Type type = p_parameter.ParameterType;
            if (type == typeof(bool)) return (_, _) => BoolValues;
            if (type.IsEnum)
            {
                string[] names = Enum.GetNames(type);
                return (_, _) => names;
            }
            return null;
        }

        private static IEnumerable<MethodInfo> FindProviders(MethodInfo p_command, string p_name) =>
            p_command.DeclaringType?.GetMethods(ProviderFlags)
                .Where(m => m.Name == p_name && typeof(IEnumerable<string>).IsAssignableFrom(m.ReturnType))
            ?? Enumerable.Empty<MethodInfo>();
        #endregion

        #region PARSING
        /// <summary>Splits a line on spaces, keeping "quoted text" together.</summary>
        public static List<string> Tokenize(string p_line) => Tokenize(p_line, out _, out _, out _);

        /// <summary>
        /// Tokenizes a line that is still being typed. <paramref name="p_partial"/> is the token under construction at
        /// the end ("" after a trailing space), <paramref name="p_partialStart"/> its index in the line (the line length
        /// after a trailing space) and <paramref name="p_quoted"/> whether it opened with a quote. When the line doesn't
        /// end in whitespace, the returned list includes that last token.
        /// </summary>
        public static List<string> Tokenize(string p_line, out string p_partial, out int p_partialStart, out bool p_quoted)
        {
            var tokens = new List<string>();
            p_partial = string.Empty;
            p_partialStart = p_line?.Length ?? 0;
            p_quoted = false;
            if (string.IsNullOrEmpty(p_line)) return tokens;

            var current = new StringBuilder();
            bool inQuotes = false;
            bool hasToken = false;
            int start = 0;
            bool startedQuoted = false;

            for (int i = 0; i < p_line.Length; i++)
            {
                char c = p_line[i];
                if (c == '"')
                {
                    if (!hasToken) { start = i; startedQuoted = true; }
                    inQuotes = !inQuotes;
                    hasToken = true; // "" is a valid empty argument
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (hasToken) tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                    startedQuoted = false;
                }
                else
                {
                    if (!hasToken) { start = i; startedQuoted = false; }
                    current.Append(c);
                    hasToken = true;
                }
            }

            if (hasToken)
            {
                // Callers that only want whole tokens (Execute) get the last one too.
                tokens.Add(current.ToString());
                p_partial = current.ToString();
                p_partialStart = start;
                p_quoted = startedQuoted;
            }

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
