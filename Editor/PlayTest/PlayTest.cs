using System.Linq;
using Cysharp.Threading.Tasks;
using Greeny.Persistence;
using Greeny.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Greeny.Editor
{
    /// <summary>
    /// Enters Play mode straight into a gameplay scene group, skipping the main menu and the intro cinematic,
    /// while still booting through the Bootstrapper (DI, SceneLoader, game state event) like the real game.
    /// <para>
    /// Start it from <c>Play Test/Selected Scene Group</c> with a SceneGroupConfig selected (also usable from the CLI via
    /// invoke_menu), or with <c>PlayTest.Play("SG_Level01")</c>
    /// from a script. When gameplay is up, the console logs
    /// <see cref="ReadyLog"/> followed by the loaded group name.
    /// </para>
    /// <para>
    /// "Scene State" entries start without a save game (nothing is bound or auto saved, so no save files are
    /// created). "Continue" loads a save the same way the Load Game menu does.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class PlayTest
    {
        /// <summary>Logged once the requested scene group has loaded and one frame has run.</summary>
        public const string ReadyLog = "[PlayTest] Ready:";

        /// <summary>
        /// True from the moment a play test is requested until Play mode exits. Runtime code can read it to skip
        /// things a test doesn't need (e.g. the startup cinematic). Same key as <see cref="IsActive"/>.
        /// </summary>
        public const string ActiveKey = "Greeny.PlayTest.Active";

        /// <summary>Pass as the save name to continue the most recently saved game.</summary>
        public const string LatestSave = "<latest>";

        const string GroupKey = "Greeny.PlayTest.Group";
        const string SaveKey = "Greeny.PlayTest.Save";
        const string PendingKey = "Greeny.PlayTest.Pending";
        const string RestoreBootstrapperKey = "Greeny.PlayTest.RestoreBootstrapperToggle";

        const string MenuRoot = "Play Test/";

        public static bool IsActive => SessionState.GetBool(ActiveKey, false);

        static PlayTest()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            // Editor closed/crashed mid-test: don't leave the user's Bootstrapper toggle flipped.
            if (!EditorApplication.isPlayingOrWillChangePlaymode) RestoreBootstrapperToggle();
        }

        #region Menu

        // Menu bar entries stay for CLI use (invoke_menu). Select a SceneGroupConfig in the Project window first.
        [MenuItem(MenuRoot + "Selected Scene Group", priority = 0)]
        static void PlaySelectedGroup() => Play(((SceneGroupConfig)Selection.activeObject).name);

        [MenuItem(MenuRoot + "Selected Scene Group", true)]
        static bool CanPlaySelectedGroup() => CanPlay() && Selection.activeObject is SceneGroupConfig;

        [MenuItem(MenuRoot + "Continue Latest Save", priority = 20)]
        static void ContinueLatest() => Play(null, LatestSave);

        [MenuItem(MenuRoot + "Continue Latest Save", true)]
        static bool CanPlay() => !EditorApplication.isPlayingOrWillChangePlaymode;

        #endregion

        /// <summary>
        /// Enters Play mode and loads <paramref name="p_groupName"/> (a SceneGroupConfig asset name listed on the
        /// SceneLoader) instead of the main menu. With <paramref name="p_saveName"/> (or <see cref="LatestSave"/>),
        /// that save is loaded instead and its own group is used; <paramref name="p_groupName"/> is then ignored.
        /// </summary>
        public static void Play(string p_groupName, string p_saveName = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[PlayTest] Already in Play mode; stop it first.");
                return;
            }
            if (string.IsNullOrEmpty(p_groupName) && string.IsNullOrEmpty(p_saveName))
            {
                Debug.LogError("[PlayTest] Pass a scene group name or a save name.");
                return;
            }

            SessionState.SetString(GroupKey, p_groupName ?? string.Empty);
            SessionState.SetString(SaveKey, p_saveName ?? string.Empty);
            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(ActiveKey, true);

            // The test relies on Play mode starting in the Bootstrapper scene; force it on for this run only.
            if (!Bootstrapper.LoadBootstrapperOnPlay)
            {
                EditorPrefs.SetBool(RestoreBootstrapperKey, true);
                Bootstrapper.LoadBootstrapperOnPlay = true;
            }

            Debug.Log($"[PlayTest] Entering Play mode → {Describe(p_groupName, p_saveName)}");
            EditorApplication.EnterPlaymode();
        }

        static string Describe(string p_group, string p_save) =>
            string.IsNullOrEmpty(p_save) ? $"group '{p_group}' (scene state)" : $"save '{p_save}'";

        static void OnPlayModeStateChanged(PlayModeStateChange p_state)
        {
            if (p_state != PlayModeStateChange.EnteredEditMode) return;

            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(PendingKey);
            SessionState.EraseString(GroupKey);
            SessionState.EraseString(SaveKey);
            RestoreBootstrapperToggle();
        }

        static void RestoreBootstrapperToggle()
        {
            if (!EditorPrefs.GetBool(RestoreBootstrapperKey, false)) return;
            EditorPrefs.DeleteKey(RestoreBootstrapperKey);
            Bootstrapper.LoadBootstrapperOnPlay = false;
        }

        // sceneLoaded for the first scene fires after its Awake/OnEnable and before Start, so the entry point can
        // be disabled before it starts loading the main menu group.
        static void OnSceneLoaded(Scene p_scene, LoadSceneMode p_mode)
        {
            if (!Application.isPlaying || !SessionState.GetBool(PendingKey, false)) return;
            if (p_scene.path != Bootstrapper.EditorScenePath) return;

            SessionState.SetBool(PendingKey, false);

            // Tests are usually driven while the Editor is unfocused (CLI); keep the game ticking.
            // Runtime-only: the project's Run In Background setting is not touched.
            Application.runInBackground = true;

            foreach (var entry in Object.FindObjectsByType<BootstrapperEntryPoint>(FindObjectsInactive.Include))
                entry.enabled = false;

            RunAsync(SessionState.GetString(GroupKey, string.Empty), SessionState.GetString(SaveKey, string.Empty)).Forget();
        }

        static async UniTaskVoid RunAsync(string p_group, string p_save)
        {
            // Let the Bootstrapper finish its first frame (injection, Start of other systems).
            await UniTask.Yield(PlayerLoopTiming.Update);
            if (!Application.isPlaying) return;

            var loader = Object.FindFirstObjectByType<SceneLoader>(FindObjectsInactive.Include);
            if (loader == null)
            {
                Debug.LogError("[PlayTest] No SceneLoader in the Bootstrapper scene; cannot load the test group.");
                return;
            }

            if (!string.IsNullOrEmpty(p_save))
            {
                if (!LoadSave(p_save)) return;
            }
            else
            {
                int index = loader.IndexOfGroup(p_group);
                if (index < 0)
                {
                    Debug.LogError($"[PlayTest] Scene group '{p_group}' is not listed on the SceneLoader.");
                    return;
                }
                loader.LoadSceneGroup(index).Forget();
            }

            // LoadSceneGroup flags itself loading synchronously; wait for the whole group (and its game state event).
            await UniTask.WaitWhile(() => Application.isPlaying && loader != null && loader.IsLoading);
            await UniTask.Yield(PlayerLoopTiming.Update);
            if (!Application.isPlaying || loader == null) return;

            Debug.Log($"{ReadyLog} {loader.CurrentGroupName ?? "(unknown group)"}");
        }

        static bool LoadSave(string p_save)
        {
            var saves = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include)
                .OfType<ISaveLoadService>()
                .FirstOrDefault();
            if (saves == null)
            {
                Debug.LogError("[PlayTest] No ISaveLoadService found; cannot load a save.");
                return false;
            }

            string name = p_save;
            if (p_save == LatestSave)
            {
                var latest = saves.GetSaves();
                if (latest == null || latest.Count == 0)
                {
                    Debug.LogError("[PlayTest] There are no saves to continue.");
                    return false;
                }
                name = latest[0].Name;
            }

            Debug.Log($"[PlayTest] Loading save '{name}'.");
            return saves.LoadGame(name);
        }
    }
}
