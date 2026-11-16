#if UNITY_EDITOR
using System;

namespace Greeny.EditorTools
{
    /// <summary>
    /// Marks a static method as a developer console command (editor only, opened with F1 in play mode).
    /// <para>
    /// Parameters are parsed from the typed arguments: string, int, float, double, bool and enums are supported,
    /// and optional parameters may be left out. A trailing <c>string</c> parameter receives the rest of the line,
    /// a trailing <c>string[]</c> receives every remaining argument. A non-void return value is printed.
    /// </para>
    /// <example><code>
    /// [DevCommand("give", "Give an item to the active character.", Suggestions = nameof(ItemNames))]
    /// private static string Give(string p_item, int p_amount = 1) { ... }
    /// </code></example>
    /// Wrap the declaring code in <c>#if UNITY_EDITOR</c>: the console and this attribute don't exist in builds.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class DevCommandAttribute : Attribute
    {
        /// <summary>What the user types. Case-insensitive, no spaces.</summary>
        public string Name { get; }

        /// <summary>Shown by <c>help</c>.</summary>
        public string Description { get; }

        /// <summary>
        /// Optional: name of a static method on the same type, returning <c>IEnumerable&lt;string&gt;</c>,
        /// that lists values for the first argument. Used for Tab completion and the hint line.
        /// </summary>
        public string Suggestions { get; set; }

        /// <summary>Optional: usage line shown by <c>help</c> and the hint line. Generated from the parameters when empty.</summary>
        public string Usage { get; set; }

        public DevCommandAttribute(string p_name, string p_description = "")
        {
            Name = p_name;
            Description = p_description;
        }
    }
}
#endif
