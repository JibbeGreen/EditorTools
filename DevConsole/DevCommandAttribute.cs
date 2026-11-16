#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Greeny.EditorTools
{
    /// <summary>
    /// Marks a static method as a developer console command (editor only, opened with F1 in play mode).
    /// <para>
    /// Parameters are parsed from the typed arguments: string, int, float, double, bool and enums are supported,
    /// and optional parameters may be left out. A trailing <c>string</c> parameter receives the rest of the line,
    /// a trailing <c>string[]</c> receives every remaining argument. A non-void return value is printed.
    /// </para>
    /// <para>
    /// Argument suggestions (the popup above the input, Tab to accept): bool and enum parameters get them for free,
    /// <see cref="DevSuggestionsAttribute"/> adds them to one parameter, and <see cref="Suggestions"/> to the command.
    /// </para>
    /// <example><code>
    /// [DevCommand("give", "Give an item to the active character.")]
    /// private static string Give([DevSuggestions(nameof(ItemNames))] string p_item, int p_amount = 1) { ... }
    /// </code></example>
    /// Wrap the declaring code in <c>#if UNITY_EDITOR</c>: the console and this attribute don't exist in builds.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class DevCommandAttribute : Attribute
    {
        /// <summary>What the user types. Case-insensitive, no spaces.</summary>
        public string Name { get; }

        /// <summary>Shown by <c>help</c> and in the hint line.</summary>
        public string Description { get; }

        /// <summary>
        /// Optional: name of a static method on the same type that lists argument values, either
        /// <c>IEnumerable&lt;string&gt; M()</c> (values for the first argument) or
        /// <c>IEnumerable&lt;string&gt; M(int p_argIndex, IReadOnlyList&lt;string&gt; p_args)</c> (any argument;
        /// <c>p_args</c> holds the arguments typed before it). Return null to fall back to the parameter's own suggestions.
        /// </summary>
        public string Suggestions { get; set; }

        /// <summary>
        /// Optional: usage line shown by <c>help</c> and the hint line, e.g. <c>"give &lt;item&gt; [amount]"</c>.
        /// Generated from the parameters when empty. Each space-separated part is highlighted while that argument is typed;
        /// a last part ending in <c>...]</c> or <c>...&gt;</c> repeats.
        /// </summary>
        public string Usage { get; set; }

        public DevCommandAttribute(string p_name, string p_description = "")
        {
            Name = p_name;
            Description = p_description;
        }
    }

    /// <summary>
    /// Suggestions for one parameter of a <see cref="DevCommandAttribute"/> method: the name of a static method on the
    /// same type, either <c>IEnumerable&lt;string&gt; M()</c> or <c>IEnumerable&lt;string&gt; M(IReadOnlyList&lt;string&gt; p_args)</c>
    /// (the arguments typed before this one). On a trailing <c>string[]</c> it applies to every remaining argument.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class DevSuggestionsAttribute : Attribute
    {
        public string Method { get; }

        public DevSuggestionsAttribute(string p_method) => Method = p_method;
    }

    /// <summary>Lists values for argument <paramref name="p_argIndex"/>; <paramref name="p_args"/> are the arguments before it.</summary>
    public delegate IEnumerable<string> DevSuggestionProvider(int p_argIndex, IReadOnlyList<string> p_args);
}
#endif
