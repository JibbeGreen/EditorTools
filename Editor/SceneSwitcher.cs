using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Greeny.Editor
{
    /// <summary>
    /// Opens scenes in the editor and adds a scene dropdown to the main toolbar.
    /// <para>
    /// The dropdown has no project-specific entries of its own: it lists the enabled build scenes, and other assemblies
    /// add sections to it with <see cref="AddMenuSection"/> (the scene group switcher and Play Test do this when their
    /// packages are installed).
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class SceneSwitcher
    {
        /// <summary>Main toolbar element id; the toolbar's context menu shows/hides it under this path.</summary>
        public const string ToolbarPath = "Greeny/Scene Switcher";

        /// <summary>Section order used by the built-in "Scenes" (build scenes) section.</summary>
        public const int BuildScenesOrder = 1000;

        static readonly List<(int order, Action<SceneMenu> populate)> s_sections = new();
        static readonly List<Func<IEnumerable<string>>> s_sceneSources = new();

        static SceneSwitcher()
        {
            AddMenuSection(BuildScenesOrder, AddBuildScenes);

            // Keep the toolbar label in sync with the active scene.
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        /// <summary>
        /// Returns the scene to open before any scene picked from the dropdown (groups and single scenes), or null for none.
        /// Unset by default; the scene group switcher sets it to the first enabled build scene (the Bootstrapper) when
        /// Greeny Scene Management is installed.
        /// </summary>
        public static Func<string> BootstrapperScenePath;

        /// <summary>False while in (or entering) Play mode, when scenes can't be switched.</summary>
        public static bool CanOpenScenes => !EditorApplication.isPlayingOrWillChangePlaymode;

        #region Opening scenes

        /// <summary>
        /// Opens one or more scenes in the editor. The first scene is opened in Single mode and becomes the active scene,
        /// the rest are opened additively.
        /// </summary>
        /// <param name="p_scenePaths">Scene asset paths, e.g. "Assets/Scenes/Level01.unity".</param>
        public static bool OpenScenes(params string[] p_scenePaths) =>
            OpenScenes(p_scenePaths, p_scenePaths != null && p_scenePaths.Length > 0 ? p_scenePaths[0] : null);

        /// <summary>
        /// Opens <paramref name="p_scenePaths"/> (first in Single mode, the rest additively, duplicates skipped) and makes
        /// <paramref name="p_activeScenePath"/> the active scene. Returns false if the user cancelled or nothing opened.
        /// </summary>
        public static bool OpenScenes(IReadOnlyList<string> p_scenePaths, string p_activeScenePath)
        {
            if (p_scenePaths == null || p_scenePaths.Count == 0)
            {
                Debug.LogError("[SceneSwitcher] No scene paths provided.");
                return false;
            }
            if (!CanOpenScenes)
            {
                Debug.LogError("[SceneSwitcher] Can't switch scenes in Play mode.");
                return false;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;

            var opened = new List<string>();
            Scene active = default;

            foreach (string path in p_scenePaths)
            {
                if (string.IsNullOrEmpty(path) || opened.Contains(path)) continue;

                Scene scene = EditorSceneManager.OpenScene(path, opened.Count == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive);
                if (!scene.IsValid())
                {
                    Debug.LogError($"[SceneSwitcher] Could not open scene: {path}");
                    continue;
                }

                opened.Add(path);
                if (path == p_activeScenePath || !active.IsValid()) active = scene;
            }

            if (opened.Count == 0) return false;

            EditorSceneManager.SetActiveScene(active);
            Debug.Log($"[SceneSwitcher] Opened {string.Join(", ", opened)} (active: {active.name})");
            return true;
        }

        /// <summary>
        /// Path of the first <em>enabled</em> scene in the build settings (build index 0 at runtime, usually the
        /// Bootstrapper), or null when there is none.
        /// </summary>
        public static string FirstEnabledBuildScenePath
        {
            get
            {
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                    if (scene.enabled && !string.IsNullOrEmpty(scene.path)) return scene.path;
                return null;
            }
        }

        #endregion

        #region Toolbar

        /// <summary>
        /// Adds a section to the toolbar dropdown. <paramref name="p_populate"/> runs every time the dropdown opens, so it
        /// can list assets created after the editor started. Sections are sorted by <paramref name="p_order"/> and
        /// separated by a line. Call it from an <c>[InitializeOnLoad]</c> static constructor.
        /// </summary>
        public static void AddMenuSection(int p_order, Action<SceneMenu> p_populate)
        {
            if (p_populate == null) return;
            s_sections.RemoveAll(s => s.populate == p_populate);
            s_sections.Add((p_order, p_populate));
            s_sections.Sort((a, b) => a.order.CompareTo(b.order));
        }

        /// <summary>
        /// Adds scene paths to the dropdown's "Scenes" list, next to the enabled build scenes (e.g. scenes that are only
        /// loaded through scene groups). <paramref name="p_source"/> runs every time the dropdown opens.
        /// </summary>
        public static void AddSceneSource(Func<IEnumerable<string>> p_source)
        {
            if (p_source == null || s_sceneSources.Contains(p_source)) return;
            s_sceneSources.Add(p_source);
        }

        [MainToolbarElement(ToolbarPath, defaultDockPosition = MainToolbarDockPosition.Middle)]
        static MainToolbarElement CreateToolbarDropdown()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            var icon = EditorGUIUtility.IconContent("SceneAsset Icon").image as Texture2D;
            var content = new MainToolbarContent(
                string.IsNullOrEmpty(sceneName) ? "Scenes" : sceneName,
                icon,
                "Open a scene group or scene");
            return new MainToolbarDropdown(content, ShowDropdown);
        }

        static void ShowDropdown(Rect p_rect)
        {
            var menu = new SceneMenu(new GenericMenu());

            foreach (var section in s_sections)
            {
                menu.BeginSection();
                try { section.populate(menu); }
                catch (Exception e) { Debug.LogException(e); }
            }

            if (menu.Menu.GetItemCount() == 0)
                menu.Menu.AddDisabledItem(new GUIContent("No scenes in the build settings"));

            menu.Menu.DropDown(p_rect);
        }

        static void OnActiveSceneChanged(Scene p_from, Scene p_to) => MainToolbar.Refresh(ToolbarPath);

        static void AddBuildScenes(SceneMenu p_menu)
        {
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                if (scene.enabled && !string.IsNullOrEmpty(scene.path)) paths.Add(scene.path);

            var extra = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in s_sceneSources)
            {
                try
                {
                    foreach (string path in source() ?? Array.Empty<string>())
                        if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) extra.Add(path);
                }
                catch (Exception e) { Debug.LogException(e); }
            }

            string activePath = SceneManager.GetActiveScene().path;
            foreach (string path in paths) AddScene(p_menu, path, activePath);

            if (paths.Count > 0 && extra.Count > 0) p_menu.AddSeparator("Scenes/");
            foreach (string path in extra) AddScene(p_menu, path, activePath);
        }

        static void AddScene(SceneMenu p_menu, string p_path, string p_activePath) =>
            p_menu.AddItem("Scenes/" + Path.GetFileNameWithoutExtension(p_path), p_path == p_activePath, CanOpenScenes,
                () => OpenWithBootstrapper(new[] { p_path }, p_path));

        /// <summary>
        /// Opens <see cref="BootstrapperScenePath"/> (when set) followed by <paramref name="p_scenePaths"/>, and makes
        /// <paramref name="p_activeScenePath"/> active. Opening the Bootstrapper scene itself opens just that scene.
        /// </summary>
        public static bool OpenWithBootstrapper(IReadOnlyList<string> p_scenePaths, string p_activeScenePath)
        {
            var paths = new List<string>();
            string bootstrapper = BootstrapperScenePath?.Invoke();
            if (!string.IsNullOrEmpty(bootstrapper)) paths.Add(bootstrapper);
            if (p_scenePaths != null) paths.AddRange(p_scenePaths);
            return OpenScenes(paths, p_activeScenePath);
        }

        #endregion
    }

    /// <summary>
    /// The toolbar dropdown a <see cref="SceneSwitcher.AddMenuSection"/> callback fills. A separator is inserted before a
    /// section's first item only, so sections that add nothing leave no empty lines.
    /// </summary>
    public sealed class SceneMenu
    {
        bool _separatorPending;

        internal SceneMenu(GenericMenu p_menu) => Menu = p_menu;

        /// <summary>The underlying menu, for anything the helpers below don't cover.</summary>
        public GenericMenu Menu { get; }

        internal void BeginSection() => _separatorPending = Menu.GetItemCount() > 0;

        /// <summary>Adds an item; "Sub/Item" paths create submenus. Disabled items are shown greyed out.</summary>
        public void AddItem(string p_path, bool p_checked, bool p_enabled, GenericMenu.MenuFunction p_action)
        {
            FlushSeparator();
            var label = new GUIContent(p_path);
            if (p_enabled) Menu.AddItem(label, p_checked, p_action);
            else Menu.AddDisabledItem(label, p_checked);
        }

        /// <summary>Adds a greyed-out line, e.g. a hint when a section has nothing to list.</summary>
        public void AddDisabledItem(string p_path)
        {
            FlushSeparator();
            Menu.AddDisabledItem(new GUIContent(p_path));
        }

        /// <summary>Adds a separator inside a submenu ("Play/").</summary>
        public void AddSeparator(string p_submenu) => Menu.AddSeparator(p_submenu);

        void FlushSeparator()
        {
            if (!_separatorPending) return;
            _separatorPending = false;
            Menu.AddSeparator(string.Empty);
        }
    }
}
