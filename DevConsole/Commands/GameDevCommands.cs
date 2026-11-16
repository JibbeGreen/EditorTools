#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Greeny.Actors;
using Greeny.Inventory;
using Unity.Cinemachine;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Greeny.EditorTools
{
    /// <summary>
    /// Abel's Eleven commands for the F1 developer console (com.greeny.editortools). Editor only.
    /// </summary>
    internal static class GameDevCommands
    {
        // Domain reload is disabled in this project: statics survive play sessions unless reset here.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _freeCamera = null;
            _cameraBrain = null;
            _noclipFacade = null;
            _items = null;
        }

        #region NOCLIP
        private static FreeFlyCamera _freeCamera;
        private static CinemachineBrain _cameraBrain;
        private static ActorFacade _noclipFacade;

        [DevCommand("noclip", "Leave your character and fly the camera through walls. Run again to return to your character.")]
        private static void Noclip()
        {
            if (_freeCamera != null)
            {
                ExitNoclip();
                DevConsole.Log("noclip OFF");
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                DevConsole.LogError("No camera tagged MainCamera in the scene.");
                return;
            }

            // Freeze the character (and Cinemachine's look input) so WASD only moves the free camera.
            _noclipFacade = Object.FindAnyObjectByType<ActorFacade>();
            if (_noclipFacade != null) _noclipFacade.SetControlsActive(false);

            // The brain would snap the camera back to the character every frame.
            _cameraBrain = camera.GetComponent<CinemachineBrain>();
            if (_cameraBrain != null) _cameraBrain.enabled = false;

            _freeCamera = camera.gameObject.AddComponent<FreeFlyCamera>();

            DevConsole.Log("noclip ON - WASD fly, E/Space up, Q down, Shift fast, Ctrl slow, scroll = speed. 'noclip' again to return.");
        }

        private static void ExitNoclip()
        {
            if (_freeCamera != null) Object.Destroy(_freeCamera);
            if (_cameraBrain != null) _cameraBrain.enabled = true; // blends back to the character's camera
            if (_noclipFacade != null) _noclipFacade.SetControlsActive(true);

            _freeCamera = null;
            _cameraBrain = null;
            _noclipFacade = null;
        }
        #endregion

        #region ITEMS
        private static List<Item> _items;

        [DevCommand("give", "Give an item to the active character. Item = asset name, display name or id; spaces optional.",
            Usage = "give <item> [amount]", Suggestions = nameof(ItemNames))]
        private static void Give(string[] p_args)
        {
            if (p_args.Length == 0)
            {
                DevConsole.LogError("Usage: give <item> [amount]");
                return;
            }

            // A trailing number is the amount, so "give red keycard 3" works without quotes.
            int amount = 1;
            int nameLength = p_args.Length;
            if (p_args.Length > 1 && int.TryParse(p_args[^1], out int parsed))
            {
                amount = parsed;
                nameLength--;
            }

            if (amount <= 0)
            {
                DevConsole.LogError("Amount must be at least 1.");
                return;
            }

            if (!TryFindItem(string.Join(" ", p_args.Take(nameLength)), out Item item)) return;
            if (!TryGetActiveInventory(out InventoryHolder inventory)) return;

            int added = inventory.Add(item, amount);
            if (added == amount) DevConsole.Log($"Gave {added}x {item.DisplayName} to {inventory.name}.");
            else if (added > 0) DevConsole.LogWarning($"Inventory full: gave {added}/{amount}x {item.DisplayName} to {inventory.name}.");
            else DevConsole.LogError($"{inventory.name}'s inventory is full.");
        }

        [DevCommand("items", "List every item in the project, optionally filtered.")]
        private static string Items(string p_filter = "")
        {
            List<Item> matches = AllItems()
                .Where(i => string.IsNullOrEmpty(p_filter) || Normalize(i.name).Contains(Normalize(p_filter))
                                                            || Normalize(i.DisplayName).Contains(Normalize(p_filter)))
                .ToList();

            if (matches.Count == 0) return "No items found.";
            return $"{matches.Count} item(s):\n" + string.Join("\n", matches.Select(i => $"  {i.name}   ({i.DisplayName}, {i.GetType().Name})"));
        }

        private static IEnumerable<string> ItemNames() => AllItems().Select(i => i.name);

        // Searched once per play session: the console is editor-only, so the AssetDatabase is the item list
        // (no ItemDatabase needed, and new items show up without being registered anywhere).
        private static List<Item> AllItems()
        {
            return _items ??= AssetDatabase.FindAssets("t:" + nameof(Item))
                .Select(AssetDatabase.GUIDToAssetPath)
                .SelectMany(p_path => (IEnumerable<Object>)AssetDatabase.LoadAllAssetsAtPath(p_path))
                .OfType<Item>()
                .Distinct()
                .OrderBy(i => i.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static bool TryFindItem(string p_query, out Item p_item) => TryFindItem(p_query, AllItems(), out p_item, true);

        /// <summary>Finds one item in <paramref name="p_items"/> by asset name, display name or id (exact first, then partial).</summary>
        internal static bool TryFindItem(string p_query, List<Item> p_items, out Item p_item, bool p_logErrors)
        {
            p_item = null;
            string key = Normalize(p_query);
            List<Item> items = p_items.Where(i => i != null).ToList();

            bool Exact(Item i) => Normalize(i.name) == key || Normalize(i.DisplayName) == key || i.Id == p_query;

            List<Item> matches = items.Where(Exact).ToList();
            if (matches.Count == 0)
                matches = items.Where(i => Normalize(i.name).Contains(key) || Normalize(i.DisplayName).Contains(key)).ToList();

            switch (matches.Count)
            {
                case 1:
                    p_item = matches[0];
                    return true;
                case 0:
                    if (p_logErrors) DevConsole.LogError($"No item matches '{p_query}'. Type 'items' for a list.");
                    return false;
                default:
                    if (p_logErrors) DevConsole.LogError($"'{p_query}' matches {matches.Count} items: {string.Join(", ", matches.Take(15).Select(i => i.name))}");
                    return false;
            }
        }

        private static string Normalize(string p_value) =>
            new string((p_value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        #endregion

        #region HELPERS
        private static bool TryGetActiveInventory(out InventoryHolder p_inventory)
        {
            p_inventory = null;

            ActorFacade facade = Object.FindAnyObjectByType<ActorFacade>();
            Pawn actor = facade != null ? facade.ActiveActor : null;
            if (actor == null)
            {
                DevConsole.LogError("No active character (is there an ActorFacade in the scene?).");
                return false;
            }

            p_inventory = actor.GetComponentInChildren<InventoryHolder>();
            if (p_inventory == null) p_inventory = actor.GetComponentInParent<InventoryHolder>();
            if (p_inventory == null)
            {
                DevConsole.LogError($"{actor.name} has no InventoryHolder.");
                return false;
            }

            return true;
        }
        #endregion
    }
}
#endif
