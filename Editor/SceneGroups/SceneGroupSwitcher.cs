using System.Collections.Generic;
using System.Linq;
using Greeny.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Greeny.Editor
{
    /// <summary>
    /// Adds every <see cref="SceneGroupConfig"/> in the project to the toolbar scene dropdown. Opening a group opens the
    /// scene that contains the <see cref="Bootstrapper"/> followed by the group's scenes, with the group's
    /// <see cref="SceneType.ActiveScene"/> scene set as active. Groups are found each time the dropdown opens, so new or
    /// renamed SceneGroupConfig assets show up without a recompile.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneGroupSwitcher
    {
        /// <summary>Section order of the group list in the dropdown (before the build scenes).</summary>
        public const int GroupsOrder = 0;

        /// <summary>Section order of the "Load Bootstrapper On Play" toggle in the dropdown.</summary>
        public const int BootstrapperToggleOrder = 500;

        /// <summary>A SceneGroupConfig asset and the label it gets in menus.</summary>
        public readonly struct GroupEntry
        {
            public readonly SceneGroupConfig Config;
            public readonly string Label;

            public GroupEntry(SceneGroupConfig p_config, string p_label)
            {
                Config = p_config;
                Label = p_label;
            }

            /// <summary>The asset name, which is what <c>SceneLoader.IndexOfGroup</c> and PlayTest look groups up by.</summary>
            public string AssetName => Config.name;
        }

        static SceneGroupSwitcher()
        {
            // Groups and single scenes open with the scene that has the Bootstrapper, so the setup can be played as is.
            SceneSwitcher.BootstrapperScenePath = () => Bootstrapper.EditorScenePath;
            SceneSwitcher.AddMenuSection(GroupsOrder, AddGroups);
            SceneSwitcher.AddMenuSection(BootstrapperToggleOrder, AddBootstrapperToggle);
            // Scenes that are only loaded through groups (not in the build list) still get a "Scenes/" entry.
            SceneSwitcher.AddSceneSource(() => FindGroups().SelectMany(g => ScenePaths(g.Config, out _)));
        }

        /// <summary>
        /// All SceneGroupConfig assets in the project, sorted by label. The label is the group's GroupName, or the asset
        /// name when GroupName is empty or shared with another group.
        /// </summary>
        public static List<GroupEntry> FindGroups()
        {
            var configs = AssetDatabase.FindAssets($"t:{nameof(SceneGroupConfig)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<SceneGroupConfig>)
                .Where(c => c != null)
                .ToList();

            var nameCounts = configs
                .GroupBy(GroupName)
                .ToDictionary(g => g.Key, g => g.Count());

            return configs
                .Select(c =>
                {
                    string name = GroupName(c);
                    string label = string.IsNullOrWhiteSpace(name) ? c.name
                        : nameCounts[name] > 1 ? $"{name} ({c.name})"
                        : name;
                    return new GroupEntry(c, label);
                })
                .OrderBy(e => e.Label, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Opens the Bootstrapper and the group's scenes. Returns false if cancelled or the group has no scenes.</summary>
        public static bool Open(SceneGroupConfig p_config)
        {
            List<string> groupScenes = ScenePaths(p_config, out string activePath);
            if (groupScenes.Count == 0)
            {
                Debug.LogError($"[SceneSwitcher] Scene group '{p_config.name}' has no valid scenes.", p_config);
                return false;
            }

            return SceneSwitcher.OpenWithBootstrapper(groupScenes, activePath ?? groupScenes[0]);
        }

        /// <summary>
        /// Asset paths of the group's scenes, in order. <paramref name="p_activePath"/> is the first scene marked
        /// <see cref="SceneType.ActiveScene"/>, or null.
        /// </summary>
        public static List<string> ScenePaths(SceneGroupConfig p_config, out string p_activePath)
        {
            p_activePath = null;
            var paths = new List<string>();
            List<SceneData> scenes = p_config != null ? p_config.Group?.Scenes : null;
            if (scenes == null) return paths;

            foreach (SceneData scene in scenes)
            {
                string path = PathOf(scene);
                if (string.IsNullOrEmpty(path)) continue;

                paths.Add(path);
                if (p_activePath == null && scene.SceneType == SceneType.ActiveScene) p_activePath = path;
            }
            return paths;
        }

        static string PathOf(SceneData p_scene)
        {
            if (p_scene?.Reference == null) return null;
            try
            {
                // Resolve through the GUID so moved/renamed scenes still open.
                string path = AssetDatabase.GUIDToAssetPath(p_scene.Reference.Guid);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (System.Exception)
            {
                return null; // Empty reference.
            }
        }

        static string GroupName(SceneGroupConfig p_config) => p_config.Group?.GroupName ?? string.Empty;

        static bool IsOpen(SceneGroupConfig p_config)
        {
            List<string> paths = ScenePaths(p_config, out _);
            return paths.Count > 0 && paths.All(p => SceneManager.GetSceneByPath(p).isLoaded);
        }

        // Same EditorPref as Scene Loading > Load Bootstrapper On Play; the Bootstrapper applies it when Play mode starts.
        static void AddBootstrapperToggle(SceneMenu p_menu) =>
            p_menu.AddItem("Load Bootstrapper On Play", Bootstrapper.LoadBootstrapperOnPlay, SceneSwitcher.CanOpenScenes,
                () => Bootstrapper.LoadBootstrapperOnPlay = !Bootstrapper.LoadBootstrapperOnPlay);

        static void AddGroups(SceneMenu p_menu)
        {
            List<GroupEntry> groups = FindGroups();
            if (groups.Count == 0)
            {
                p_menu.AddDisabledItem("No scene groups (Create > Greeny > SceneManagement > SceneGroupConfig)");
                return;
            }

            foreach (GroupEntry group in groups)
            {
                SceneGroupConfig config = group.Config;
                p_menu.AddItem(group.Label, IsOpen(config), SceneSwitcher.CanOpenScenes, () => Open(config));
            }
        }
    }
}
