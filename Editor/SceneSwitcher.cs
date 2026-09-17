using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Greeny.Editor {

    public static class SceneSwitcher
    {
        private const string MainMenuScene = "Assets/Scenes/MainMenu.unity";
        private const string GameplayScene = "Assets/Scenes/Gameplay.unity";
        private const string Level01Scene = "Assets/Scenes/Level01.unity";
        private const string BootstrapperScene = "Assets/Scenes/Bootstrapper.unity";
        private const string UIScene = "Assets/Scenes/UI.unity";

        /// <summary>
        /// Opens one or more scenes in the editor. 
        /// First scene is opened in Single mode (becomes active scene), rest are additive.
        /// </summary>
        /// <param name="scenePaths">Array of scene asset paths (relative to Assets folder)</param>
        public static void OpenScenes(params string[] scenePaths)
        {
            if (scenePaths == null || scenePaths.Length == 0)
            {
                Debug.LogError("[SceneSwitcher] No scene paths provided!");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return; // User cancelled

            Scene firstScene = EditorSceneManager.OpenScene(scenePaths[0], OpenSceneMode.Single);
            if (!firstScene.IsValid())
            {
                Debug.LogError($"[SceneSwitcher] Could not open scene: {scenePaths[0]}");
                return;
            }

            // Open remaining scenes additively
            for (int i = 1; i < scenePaths.Length; i++)
            {
                Scene additiveScene = EditorSceneManager.OpenScene(scenePaths[i], OpenSceneMode.Additive);
                if (!additiveScene.IsValid())
                {
                    Debug.LogError($"[SceneSwitcher] Could not open scene: {scenePaths[i]}");
                }
            }

            EditorSceneManager.SetActiveScene(firstScene);
            Debug.Log($"[SceneSwitcher] Loaded scenes: " +
                $"{string.Join(", ", scenePaths)}");
        }

        [MenuItem("Switch Scene/MainMenu")]
        public static void LoadMainMenu()
        {
            OpenScenes(
                MainMenuScene
            );
        }

        [MenuItem("Switch Scene/MainGame")]
        public static void LoadMainGame()
        {
            OpenScenes(
                GameplayScene,
                Level01Scene,
                UIScene,
                BootstrapperScene
            );
        }

        [MenuItem("Switch Scene/Bootstrapper")]
        public static void LoadBootstrapperScene()
        {
            OpenScenes(
                BootstrapperScene
            );
        }

        [MenuItem("Switch Scene/UI")]
        public static void LoadUIScene()
        {
            OpenScenes(
                UIScene
            );
        }
    }
}
