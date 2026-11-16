#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Greeny.Actors;
using Greeny.Core;
using Greeny.Inventory;
using Greeny.Utils;
using Reflex.Core;
using Reflex.Extensions;
using Reflex.Injectors;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Greeny.EditorTools
{
    /// <summary>
    /// heal / kill / damage / clearinventory / spawn for the F1 developer console. Editor only.
    /// <para>
    /// Targets work like chat-command selectors and default to the player (the possessed character):
    /// <c>@p</c> player, <c>@e</c> everyone, <c>@o</c> everyone but the player, <c>@n</c> nearest to the player,
    /// <c>@t</c> whoever is under the crosshair, <c>@r</c> someone random, or a name ("guard", or "guard*" for every match).
    /// </para>
    /// </summary>
    internal static class EntityDevCommands
    {
        private const string TargetHelp = "Target: @p player (default), @e all, @o all but player, @n nearest, @t crosshair, @r random, or a name (name* = every match).";
        private const float CrosshairRange = 200f;

        // Domain reload is disabled in this project: statics survive play sessions unless reset here.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _characterPrefabs = null;

        #region COMMANDS
        [DevCommand("heal", "Restore health (full when no amount is given). " + TargetHelp,
            Usage = "heal [amount=full] [target=@p]", Suggestions = nameof(HealSuggestions))]
        private static void Heal(string[] p_args)
        {
            // "heal", "heal 25", "heal @t", "heal 25 guard"
            float amount = -1f;
            int targetStart = 0;
            if (p_args.Length > 0 && TryParseNumber(p_args[0], out float parsed))
            {
                amount = parsed;
                targetStart = 1;
            }

            if (!TryResolve(string.Join(" ", p_args.Skip(targetStart)), out List<Entity> targets)) return;

            var healed = new List<string>();
            foreach (Entity target in targets)
            {
                if (target.Health == null) continue;
                bool wasDead = target.Health.IsDead;
                float before = target.Health.CurrentHealth;
                target.Health.Heal(amount > 0f ? amount : target.Health.MaxHealth);
                float gained = target.Health.CurrentHealth - before;
                healed.Add($"{target.Name} +{Format(gained)} ({Format(target.Health.CurrentHealth)}/{Format(target.Health.MaxHealth)})" +
                           (wasDead ? " revived: health only, AI and animation stay dead" : string.Empty));
            }

            Report("Healed", healed, "Nothing to heal.");
        }

        [DevCommand("kill", "Kill instantly, even when invincible. " + TargetHelp,
            Usage = "kill [target=@p]", Suggestions = nameof(FirstArgTargets))]
        private static void Kill(string p_target = "@p")
        {
            if (!TryResolve(p_target, out List<Entity> targets)) return;

            var killed = new List<string>();
            foreach (Entity target in targets)
            {
                if (target.Health == null || target.Health.IsDead) continue;
                target.Health.Kill();
                killed.Add(target.Name);
            }

            Report("Killed", killed, "Nothing to kill (already dead?).");
        }

        [DevCommand("damage", "Deal damage (respects invincibility). " + TargetHelp,
            Usage = "damage <amount> [target=@p]", Suggestions = nameof(SecondArgTargets))]
        private static void Damage(float p_amount, string p_target = "@p")
        {
            if (p_amount <= 0f)
            {
                DevConsole.LogError("Amount must be above 0.");
                return;
            }

            if (!TryResolve(p_target, out List<Entity> targets)) return;

            var damaged = new List<string>();
            foreach (Entity target in targets)
            {
                if (target.Health == null || target.Health.IsDead) continue;
                if (target.Health.Invincible)
                {
                    DevConsole.LogWarning($"{target.Name} is invincible.");
                    continue;
                }

                float before = target.Health.CurrentHealth;
                target.Health.TakeDamage(new DamageInfo(p_amount, null));
                damaged.Add($"{target.Name} -{Format(before - target.Health.CurrentHealth)}" +
                            (target.Health.IsDead ? " (died)" : $" ({Format(target.Health.CurrentHealth)}/{Format(target.Health.MaxHealth)})"));
            }

            Report("Damaged", damaged, "Nothing took damage.");
        }

        [DevCommand("clearinventory", "Empty an inventory, or remove only one item. " + TargetHelp,
            Usage = "clearinventory [target=@p] [item]", Suggestions = nameof(ClearInventorySuggestions))]
        [DevCommand("clearinv", "Short for clearinventory. " + TargetHelp,
            Usage = "clearinv [target=@p] [item]", Suggestions = nameof(ClearInventorySuggestions))]
        private static void ClearInventory(string p_target = "@p", string p_item = "")
        {
            if (!TryResolve(p_target, out List<Entity> targets)) return;

            Item item = null;
            if (!string.IsNullOrWhiteSpace(p_item))
            {
                // Prefer items the targets actually hold, so a partial name picks the right one.
                List<Item> held = targets.Select(t => t.Inventory).Where(i => i != null)
                                         .SelectMany(i => i.Slots.Select(s => s.Item)).Distinct().ToList();
                if (!GameDevCommands.TryFindItem(p_item, held, out item, false) && !GameDevCommands.TryFindItem(p_item, out item)) return;
            }

            var cleared = new List<string>();
            foreach (Entity target in targets)
            {
                InventoryHolder inventory = target.Inventory;
                if (inventory == null)
                {
                    if (targets.Count == 1) DevConsole.LogError($"{target.Name} has no InventoryHolder.");
                    continue;
                }

                if (item != null)
                {
                    int removed = inventory.RemoveAll(item);
                    if (removed > 0) cleared.Add($"{target.Name} -{removed}x {item.DisplayName}");
                    continue;
                }

                int stacks = inventory.Slots.Count;
                inventory.Clear();
                cleared.Add($"{target.Name} ({stacks} stack{(stacks == 1 ? "" : "s")})");
            }

            Report("Cleared", cleared, item != null ? $"Nobody had {item.DisplayName}." : "No inventories found.");
        }

        [DevCommand("spawn", "Spawn a character prefab under the crosshair (or in front of the player). " +
                             "Name = prefab name, spaces optional, partial is fine.",
            Usage = "spawn <character> [count=1]", Suggestions = nameof(SpawnSuggestions))]
        private static void Spawn(string[] p_args)
        {
            if (p_args.Length == 0)
            {
                DevConsole.LogError("Usage: spawn <character> [count]. Type 'characters' for a list.");
                return;
            }

            // A trailing number is the count, so "spawn GUARD - Arnold 3" works without quotes.
            int count = 1;
            int nameLength = p_args.Length;
            if (p_args.Length > 1 && int.TryParse(p_args[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                count = parsed;
                nameLength--;
            }

            if (count < 1 || count > 50)
            {
                DevConsole.LogError("Count must be between 1 and 50.");
                return;
            }

            if (!TryFindCharacter(string.Join(" ", p_args.Take(nameLength)), out GameObject prefab)) return;

            Vector3 center = GetSpawnPoint(out Vector3 facing);
            Scene scene = SceneManager.GetActiveScene();
            bool usesNavMesh = prefab.GetComponentInChildren<NavMeshAgent>(true) != null;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                // Several at once stand in a ring so they don't spawn inside each other.
                Vector3 position = center;
                if (count > 1)
                {
                    float angle = i * Mathf.PI * 2f / count;
                    float radius = 0.6f + 0.25f * count;
                    position += new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                }

                if (usesNavMesh && NavMesh.SamplePosition(position, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                    position = navHit.position;

                if (SpawnCharacter(prefab, scene, position, Quaternion.LookRotation(facing, Vector3.up)) != null) spawned++;
            }

            if (spawned > 0) DevConsole.Log($"Spawned {spawned}x {prefab.name} at {center.ToString("F1")} in '{scene.name}'.");
        }

        [DevCommand("characters", "List every character prefab 'spawn' can create, optionally filtered.")]
        private static string Characters(string p_filter = "")
        {
            List<GameObject> matches = CharacterPrefabs()
                .Where(p => string.IsNullOrEmpty(p_filter) || Normalize(p.name).Contains(Normalize(p_filter)))
                .ToList();

            if (matches.Count == 0) return "No character prefabs found.";
            return $"{matches.Count} character prefab(s):\n" +
                   string.Join("\n", matches.Select(p => $"  {p.name}   ({p.GetComponent<Actor>().GetType().Name}, {AssetDatabase.GetAssetPath(p)})"));
        }
        #endregion

        #region SUGGESTIONS
        private static readonly string[] Selectors = { "@p", "@e", "@o", "@n", "@t", "@r" };

        private static IEnumerable<string> TargetSuggestions() =>
            Selectors.Concat(AllEntities().Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        private static IEnumerable<string> FirstArgTargets(int p_index, IReadOnlyList<string> p_args) =>
            p_index == 0 ? TargetSuggestions() : Enumerable.Empty<string>();

        private static IEnumerable<string> SecondArgTargets(int p_index, IReadOnlyList<string> p_args) =>
            p_index == 1 ? TargetSuggestions() : Enumerable.Empty<string>();

        // heal [amount] [target]: targets come first unless a number was typed.
        private static IEnumerable<string> HealSuggestions(int p_index, IReadOnlyList<string> p_args)
        {
            if (p_index == 0) return TargetSuggestions();
            if (p_index == 1 && TryParseNumber(p_args[0], out _)) return TargetSuggestions();
            return Enumerable.Empty<string>();
        }

        // clearinventory [target] [item]: the item list is what that target is carrying.
        private static IEnumerable<string> ClearInventorySuggestions(int p_index, IReadOnlyList<string> p_args)
        {
            if (p_index == 0) return TargetSuggestions();
            if (p_index != 1) return Enumerable.Empty<string>();

            if (!TryResolve(p_args[0], out List<Entity> targets, false)) return Enumerable.Empty<string>();
            return targets.Select(t => t.Inventory).Where(i => i != null)
                          .SelectMany(i => i.Slots.Select(s => s.Item)).Where(i => i != null)
                          .Select(i => i.name).Distinct();
        }

        private static IEnumerable<string> SpawnSuggestions(int p_index, IReadOnlyList<string> p_args) =>
            p_index == 0 ? CharacterPrefabs().Select(p => p.name) : Enumerable.Empty<string>();
        #endregion

        #region TARGETS
        private sealed class Entity
        {
            public GameObject Root;
            public Health Health;
            public string Name => Root.name;

            public InventoryHolder Inventory
            {
                get
                {
                    InventoryHolder inventory = Root.GetComponentInChildren<InventoryHolder>();
                    return inventory != null ? inventory : Root.GetComponentInParent<InventoryHolder>();
                }
            }
        }

        // Every actor plus anything else with Health (props, turrets...). Inactive objects are skipped.
        private static List<Entity> AllEntities()
        {
            var entities = new List<Entity>();
            var seen = new HashSet<GameObject>();

            foreach (Actor actor in Registry<Actor>.All.ToList())
            {
                if (actor == null || !actor.gameObject.activeInHierarchy || !seen.Add(actor.gameObject)) continue;
                entities.Add(new Entity { Root = actor.gameObject, Health = actor.Health });
            }

            foreach (Health health in Object.FindObjectsByType<Health>())
            {
                if (health.GetComponentInParent<Actor>() != null || !seen.Add(health.gameObject)) continue;
                entities.Add(new Entity { Root = health.gameObject, Health = health });
            }

            return entities;
        }

        private static Pawn ActivePlayer()
        {
            ActorFacade facade = Object.FindAnyObjectByType<ActorFacade>();
            return facade != null ? facade.ActiveActor : null;
        }

        // The possessed character and the parasite itself (it rides along inside its host).
        private static bool IsPlayer(Entity p_entity, Pawn p_player) =>
            (p_player != null && p_entity.Root == p_player.gameObject) || p_entity.Root.GetComponent<Hero>() != null;

        private static bool TryResolve(string p_query, out List<Entity> p_targets, bool p_logErrors = true)
        {
            p_targets = new List<Entity>();
            string query = string.IsNullOrWhiteSpace(p_query) ? "@p" : p_query.Trim();
            Pawn player = ActivePlayer();
            List<Entity> all = AllEntities();
            List<Entity> others = all.Where(e => !IsPlayer(e, player)).ToList();
            List<Entity> livingOthers = others.Where(e => e.Health == null || !e.Health.IsDead).ToList();

            string error = null;
            switch (query.ToLowerInvariant())
            {
                case "@p":
                case "@s":
                    if (player == null) error = "No active player (is there an ActorFacade in the scene?).";
                    else p_targets.Add(all.FirstOrDefault(e => e.Root == player.gameObject) ??
                                       new Entity { Root = player.gameObject, Health = player.Health });
                    break;

                case "@e":
                    p_targets.AddRange(all);
                    break;

                case "@o":
                    p_targets.AddRange(others);
                    break;

                case "@n":
                {
                    Vector3 origin = player != null ? player.transform.position : Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                    Entity nearest = livingOthers.OrderBy(e => (e.Root.transform.position - origin).sqrMagnitude).FirstOrDefault();
                    if (nearest != null) p_targets.Add(nearest);
                    else error = "Nobody else is alive nearby.";
                    break;
                }

                case "@r":
                    if (livingOthers.Count > 0) p_targets.Add(livingOthers[Random.Range(0, livingOthers.Count)]);
                    else error = "Nobody else is alive.";
                    break;

                case "@t":
                {
                    Entity looked = EntityUnderCrosshair(all, player);
                    if (looked != null) p_targets.Add(looked);
                    else error = "Not looking at anything with health.";
                    break;
                }

                default:
                    error = ResolveByName(query, all, p_targets);
                    break;
            }

            if (error == null && p_targets.Count == 0) error = $"No targets for '{query}'.";
            if (error != null)
            {
                if (p_logErrors) DevConsole.LogError(error);
                return false;
            }
            return true;
        }

        // Exact name (all matches) > "name*" (all containing) > the single name containing it.
        private static string ResolveByName(string p_query, List<Entity> p_all, List<Entity> p_targets)
        {
            bool wildcard = p_query.EndsWith("*");
            string key = Normalize(p_query);
            if (key.Length == 0) return $"'{p_query}' isn't a target.";

            List<Entity> exact = p_all.Where(e => Normalize(e.Name) == key).ToList();
            List<Entity> partial = p_all.Where(e => Normalize(e.Name).Contains(key)).ToList();

            if (!wildcard && exact.Count > 0) { p_targets.AddRange(exact); return null; }
            if (wildcard || partial.Count == 1) { p_targets.AddRange(partial); return partial.Count > 0 ? null : $"Nothing matches '{p_query}'."; }
            if (partial.Count == 0) return $"Nothing matches '{p_query}'. Use @p, @e, @o, @n, @t, @r or a name.";

            return $"'{p_query}' matches {partial.Count}: {string.Join(", ", partial.Take(10).Select(e => e.Name))}" +
                   $"{(partial.Count > 10 ? ", ..." : "")}. Use '{p_query.TrimEnd('*')}*' for all of them.";
        }

        // The first thing the camera's centre ray hits that isn't the player.
        private static Entity EntityUnderCrosshair(List<Entity> p_all, Pawn p_player)
        {
            if (!TryCrosshairHit(p_player, out RaycastHit hit)) return null;

            Actor actor = hit.collider.GetComponentInParent<Actor>();
            Health health = hit.collider.GetComponentInParent<Health>();
            GameObject root = actor != null ? actor.gameObject : health != null ? health.gameObject : null;
            return root != null ? p_all.FirstOrDefault(e => e.Root == root) : null;
        }

        private static bool TryCrosshairHit(Pawn p_player, out RaycastHit p_hit)
        {
            p_hit = default;
            Camera camera = Camera.main;
            if (camera == null) return false;

            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit[] hits = Physics.RaycastAll(ray, CrosshairRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                // Third person: the ray starts behind the player, so skip its own colliders.
                if (p_player != null && hit.collider.transform.IsChildOf(p_player.transform)) continue;
                if (hit.collider.GetComponentInParent<Hero>() != null) continue;
                p_hit = hit;
                return true;
            }
            return false;
        }
        #endregion

        #region SPAWNING
        private static List<GameObject> _characterPrefabs;

        // Every prefab whose root is an Actor. Searched once per play session (editor only, so the AssetDatabase is the list).
        private static List<GameObject> CharacterPrefabs()
        {
            return _characterPrefabs ??= AssetDatabase.FindAssets("t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .Where(p => p != null && p.GetComponent<Actor>() != null)
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryFindCharacter(string p_query, out GameObject p_prefab)
        {
            p_prefab = null;
            string key = Normalize(p_query);
            List<GameObject> prefabs = CharacterPrefabs();

            List<GameObject> matches = prefabs.Where(p => Normalize(p.name) == key).ToList();
            if (matches.Count == 0) matches = prefabs.Where(p => Normalize(p.name).Contains(key)).ToList();

            switch (matches.Count)
            {
                case 1:
                    p_prefab = matches[0];
                    return true;
                case 0:
                    DevConsole.LogError($"No character prefab matches '{p_query}'. Type 'characters' for a list.");
                    return false;
                default:
                    DevConsole.LogError($"'{p_query}' matches {matches.Count} prefabs: {string.Join(", ", matches.Take(15).Select(p => p.name))}");
                    return false;
            }
        }

        // Under the crosshair when it hits something close enough, otherwise 3 m in front of the player.
        private static Vector3 GetSpawnPoint(out Vector3 p_facing)
        {
            Pawn player = ActivePlayer();
            Camera camera = Camera.main;
            Transform viewer = camera != null ? camera.transform : player != null ? player.transform : null;

            Vector3 point;
            if (TryCrosshairHit(player, out RaycastHit hit) && hit.distance < 60f)
                point = hit.point + hit.normal * 0.05f;
            else if (player != null)
                point = player.transform.position + Flat(viewer != null ? viewer.forward : player.transform.forward) * 3f;
            else
                point = viewer != null ? viewer.position + viewer.forward * 3f : Vector3.zero;

            // Face whoever spawned them.
            Vector3 look = viewer != null ? Flat(viewer.position - point) : Vector3.forward;
            p_facing = look.sqrMagnitude > 0.001f ? look : Vector3.forward;
            return point;
        }

        private static Vector3 Flat(Vector3 p_direction)
        {
            p_direction.y = 0f;
            return p_direction.sqrMagnitude > 0.0001f ? p_direction.normalized : Vector3.forward;
        }

        // Same steps as DisplacedActors: instantiate under an inactive holder so Awake waits for a fresh id and injection.
        private static Actor SpawnCharacter(GameObject p_prefab, Scene p_scene, Vector3 p_position, Quaternion p_rotation)
        {
            var holder = new GameObject("[Spawning Actor]");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, p_scene);

            try
            {
                GameObject instance = Object.Instantiate(p_prefab, p_position, p_rotation, holder.transform);
                instance.name = p_prefab.name;

                // Prefab instances share the prefab's serialized ids; the save system needs unique ones.
                Actor actor = instance.GetComponent<Actor>();
                actor.Id = SerializableGuid.NewGuid();
                actor.SourcePrefabGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(p_prefab));
                foreach (InventoryHolder inventory in instance.GetComponentsInChildren<InventoryHolder>(true))
                    inventory.Id = SerializableGuid.NewGuid();

                Container container = GetContainer(p_scene);
                if (container != null) GameObjectInjector.InjectRecursive(instance, container);

                instance.transform.SetParent(null, true); // stays in p_scene and becomes active: Awake runs now
                return actor;
            }
            catch (Exception e)
            {
                DevConsole.LogError($"Spawning {p_prefab.name} failed: {e.Message}");
                Debug.LogException(e);
                return null;
            }
            finally
            {
                Object.Destroy(holder);
            }
        }

        private static Container GetContainer(Scene p_scene)
        {
            try { return p_scene.GetSceneContainer(); }
            catch { return Container.RootContainer; }
        }
        #endregion

        #region HELPERS
        private static void Report(string p_verb, List<string> p_done, string p_nothing)
        {
            if (p_done.Count == 0) DevConsole.LogWarning(p_nothing);
            else if (p_done.Count <= 6) DevConsole.Log($"{p_verb} {string.Join(", ", p_done)}.");
            else DevConsole.Log($"{p_verb} {p_done.Count}:\n  " + string.Join("\n  ", p_done));
        }

        private static bool TryParseNumber(string p_value, out float p_number) =>
            float.TryParse(p_value, NumberStyles.Float, CultureInfo.InvariantCulture, out p_number);

        private static string Format(float p_value) => p_value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Normalize(string p_value) =>
            new string((p_value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        #endregion
    }
}
#endif
