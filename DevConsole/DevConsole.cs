#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Greeny.EditorTools
{
    /// <summary>
    /// In-game developer console, editor only. Press F1 in play mode to open it, type a command, Enter to run.
    /// <para>
    /// Commands are static methods marked with <see cref="DevCommandAttribute"/> anywhere in the project, or
    /// added at runtime through <see cref="DevCommandRegistry.Register"/>. Type <c>help</c> to list them.
    /// </para>
    /// Keys: Enter runs, Up/Down browse history, Tab completes, Esc or F1 closes.
    /// While open, keyboard and mouse are disconnected from the Input System so typing never moves the
    /// player or triggers hotkeys; they reconnect when it closes. Created automatically when play mode starts.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class DevConsole : MonoBehaviour
    {
        #region CONSTANTS
        public const KeyCode ToggleKey = KeyCode.F1;

        private const int MaxLines = 500;
        private const int MaxHistory = 100;
        private const int MaxListedMatches = 40;
        private const float HeightFraction = 0.45f;
        private const string InputControlName = "Greeny.DevConsole.Input";
        #endregion

        private enum LineKind { Echo, Info, Warning, Error }

        #region STATIC STATE
        private static DevConsole _instance;
        private static readonly List<(string text, LineKind kind)> _lines = new();
        // Survives play sessions on purpose (domain reload may be disabled): Up recalls last session's commands.
        private static readonly List<string> _history = new();
        // Devices this console disabled. Static so they can still be re-enabled if the instance dies unexpectedly.
        private static readonly List<InputDevice> _suspendedDevices = new();

        /// <summary>True while the console is visible and taking keyboard input.</summary>
        public static bool IsOpen { get; private set; }

        public static event Action Opened;
        public static event Action Closed;
        #endregion

        #region INSTANCE STATE
        private string _input = string.Empty;
        private int _historyIndex = -1; // -1 = not browsing
        private Vector2 _scroll;
        private bool _scrollToBottom;
        private bool _focusInput;
        private bool _moveCaretToEnd;
        private bool _waitForToggleRelease; // the key that opened the console is still held
        private bool _closeOnRelease;       // close once Esc/F1 is released, so the release can't reach gameplay input

        private CursorLockMode _previousLockState;
        private bool _previousCursorVisible;

        private string _hintSource;
        private string _hint = string.Empty;

        private Texture2D _panelTexture;
        private Texture2D _inputTexture;
        private GUIStyle _lineStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _hintStyle;
        private Font _font;
        #endregion

        #region BOOTSTRAP
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ResumeDevices();
            _instance = null;
            IsOpen = false;
            Opened = null;
            Closed = null;
            _lines.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            var go = new GameObject("[DevConsole]") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DevConsole>();
        }

        [InitializeOnLoadMethod]
        private static void HookPlayModeExit()
        {
            // Last line of defence: never leave the keyboard/mouse disabled in edit mode.
            EditorApplication.playModeStateChanged += p_state =>
            {
                if (p_state == PlayModeStateChange.EnteredEditMode) ResumeDevices();
            };
        }
        #endregion

        #region PUBLIC API
        public static void Log(string p_message) => AddLine(p_message, LineKind.Info);
        public static void LogWarning(string p_message) => AddLine(p_message, LineKind.Warning);
        public static void LogError(string p_message) => AddLine(p_message, LineKind.Error);

        public static void Clear() => _lines.Clear();

        /// <summary>Runs a command line as if it was typed (it is echoed and logged, but not added to history).</summary>
        public static void Execute(string p_line)
        {
            if (string.IsNullOrWhiteSpace(p_line)) return;
            AddLine("> " + p_line.Trim(), LineKind.Echo);
            DevCommandRegistry.Execute(p_line);
        }

        public static void Open()
        {
            if (_instance != null) _instance.OpenInternal();
        }

        public static void Close()
        {
            if (_instance != null) _instance.CloseInternal();
        }
        #endregion

        #region UNITY METHODS
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void OnDisable() => CloseInternal();

        private void OnDestroy()
        {
            CloseInternal();
            if (_panelTexture != null) Destroy(_panelTexture);
            if (_inputTexture != null) Destroy(_inputTexture);
            if (_font != null) Destroy(_font);
            if (_instance == this) _instance = null;
        }

        private void OnGUI()
        {
            Event e = Event.current;

            HandleToggleKeys(e);
            // Used: the toggle key opened us during this event, so its Layout pass ran while closed and GUILayout
            // would have no layout data. Draw from the next event on.
            if (!IsOpen || e.type == EventType.Used) return;

            EnsureStyles();

            // Scale with the game view so the text stays readable from 720p to 4K.
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 3f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.depth = -1000;

            float width = Screen.width / scale;
            float height = Screen.height * HeightFraction / scale;
            const float pad = 8f;
            float inputHeight = _inputStyle.lineHeight + 10f;
            float hintHeight = _hintStyle.lineHeight + 6f;

            GUI.DrawTexture(new Rect(0, 0, width, height), _panelTexture);

            // Output
            var outputRect = new Rect(pad, pad, width - pad * 2, height - inputHeight - hintHeight - pad * 3);
            if (_scrollToBottom) _scroll.y = float.MaxValue;
            GUILayout.BeginArea(outputRect);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            foreach ((string text, LineKind kind) in _lines)
            {
                _lineStyle.normal.textColor = ColorFor(kind);
                GUILayout.Label(text, _lineStyle);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            if (e.type == EventType.Repaint) _scrollToBottom = false;

            // After the GUILayout block: running a command adds output lines, and changing the line count between
            // an event's Layout pass and the event itself breaks GUILayout. Before the text field, so it never
            // sees Enter/Tab/arrows.
            HandleEditingKeys(e);
            if (!IsOpen)
            {
                GUI.matrix = previousMatrix; // a command closed the console
                return;
            }

            // Input
            var inputRect = new Rect(pad, height - inputHeight - hintHeight - pad, width - pad * 2, inputHeight);
            GUI.DrawTexture(inputRect, _inputTexture);
            GUI.SetNextControlName(InputControlName);
            string edited = GUI.TextField(inputRect, _input, _inputStyle);
            if (edited != _input)
            {
                _input = edited;
                _historyIndex = -1;
            }

            if (_focusInput)
            {
                GUI.FocusControl(InputControlName);
                _focusInput = false;
                _moveCaretToEnd = true;
            }

            if (_moveCaretToEnd && GUI.GetNameOfFocusedControl() == InputControlName)
            {
                var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                editor.text = _input;
                editor.MoveTextEnd();
                _moveCaretToEnd = false;
            }

            // Hint line: matching commands, or the usage + suggestions of the command being typed.
            RefreshHint();
            GUI.Label(new Rect(pad, height - hintHeight - pad + 2f, width - pad * 2, hintHeight), _hint, _hintStyle);

            GUI.matrix = previousMatrix;
        }
        #endregion

        #region OPEN / CLOSE
        private void OpenInternal()
        {
            if (IsOpen) return;
            IsOpen = true;

            SuspendDevices();

            _previousLockState = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            _focusInput = true;
            _scrollToBottom = true;
            _closeOnRelease = false;

            if (_lines.Count == 0) Log("Developer console. Type 'help' for commands. Tab completes, Esc closes.");

            Opened?.Invoke();
        }

        private void CloseInternal()
        {
            if (!IsOpen) return;
            IsOpen = false;

            ResumeDevices();

            Cursor.lockState = _previousLockState;
            Cursor.visible = _previousCursorVisible;

            GUIUtility.keyboardControl = 0;
            _closeOnRelease = false;
            _waitForToggleRelease = false;

            Closed?.Invoke();
        }

        // Disabling the devices (not the actions) keeps every action's enabled state untouched, so a command that
        // changes it (e.g. noclip turning player controls off) isn't undone when the console closes.
        // IMGUI keeps receiving keys: in the editor it gets them from the game view, not from the Input System.
        private static void SuspendDevices()
        {
            foreach (InputDevice device in InputSystem.devices)
            {
                if (device is not (Keyboard or Mouse) || !device.enabled) continue;
                InputSystem.DisableDevice(device);
                _suspendedDevices.Add(device);
            }
        }

        private static void ResumeDevices()
        {
            foreach (InputDevice device in _suspendedDevices)
            {
                if (device == null || !device.added || device.enabled) continue;
                try { InputSystem.EnableDevice(device); }
                catch (Exception e) { Debug.LogException(e); }
            }
            _suspendedDevices.Clear();
        }

        // Opens on F1 down. Closes on F1/Esc *release*: the devices reconnect after the key is up, so the
        // key can't reach gameplay (Esc would otherwise open the pause menu on the same press).
        private void HandleToggleKeys(Event p_event)
        {
            bool isToggle = p_event.keyCode == ToggleKey || (IsOpen && p_event.keyCode == KeyCode.Escape);
            if (!isToggle) return;

            if (p_event.type == EventType.KeyDown)
            {
                if (!IsOpen)
                {
                    OpenInternal();
                    _waitForToggleRelease = true;
                }
                else if (!_waitForToggleRelease)
                {
                    _closeOnRelease = true;
                }
                p_event.Use();
            }
            else if (p_event.type == EventType.KeyUp)
            {
                _waitForToggleRelease = false;
                if (_closeOnRelease) CloseInternal();
                if (IsOpen) p_event.Use();
            }
        }
        #endregion

        #region EDITING
        private void HandleEditingKeys(Event p_event)
        {
            if (p_event.type != EventType.KeyDown) return;

            switch (p_event.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit();
                    p_event.Use();
                    break;
                case KeyCode.UpArrow:
                    BrowseHistory(-1);
                    p_event.Use();
                    break;
                case KeyCode.DownArrow:
                    BrowseHistory(+1);
                    p_event.Use();
                    break;
                case KeyCode.Tab:
                    Autocomplete();
                    p_event.Use();
                    break;
                default:
                    // The text field must not see these: Tab / Enter also arrive as separate character events.
                    if (p_event.character is '\t' or '\n' or '\r') p_event.Use();
                    break;
            }
        }

        private void Submit()
        {
            string line = _input.Trim();
            _input = string.Empty;
            _historyIndex = -1;
            _scrollToBottom = true;
            _focusInput = true;
            if (line.Length == 0) return;

            if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);

            Execute(line);
        }

        private void BrowseHistory(int p_direction)
        {
            if (_history.Count == 0) return;

            if (p_direction < 0)
            {
                _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Mathf.Max(0, _historyIndex - 1);
            }
            else
            {
                if (_historyIndex < 0) return;
                _historyIndex++;
                if (_historyIndex >= _history.Count)
                {
                    _historyIndex = -1;
                    _input = string.Empty;
                    _moveCaretToEnd = true;
                    return;
                }
            }

            _input = _history[_historyIndex];
            _moveCaretToEnd = true;
        }

        private void Autocomplete()
        {
            int space = _input.IndexOf(' ');

            // Completing the command name.
            if (space < 0)
            {
                string typed = _input.Trim();
                List<string> names = DevCommandRegistry.All
                    .Select(c => c.Name)
                    .Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                ApplyCompletion(names, typed, p_match => p_match + " ", p_prefix => p_prefix);
                return;
            }

            // Completing the first argument.
            string commandName = _input.Substring(0, space);
            if (!DevCommandRegistry.TryGet(commandName, out DevCommand command) || !command.HasSuggestions) return;

            string partial = _input.Substring(space + 1).TrimStart().Trim('"');
            List<string> options = command.GetSuggestions().ToList();
            List<string> matches = options.Where(o => o.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = options.Where(o => o.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            ApplyCompletion(matches, partial,
                p_match => $"{command.Name} {Quote(p_match)} ",
                p_prefix => $"{command.Name} {p_prefix}");
        }

        private void ApplyCompletion(List<string> p_matches, string p_typed, Func<string, string> p_full, Func<string, string> p_partial)
        {
            if (p_matches.Count == 0) return;

            if (p_matches.Count == 1)
            {
                _input = p_full(p_matches[0]);
                _moveCaretToEnd = true;
                return;
            }

            // Several matches: extend to their shared prefix and list them.
            string common = CommonPrefix(p_matches);
            if (common.Length > p_typed.Length && common.StartsWith(p_typed, StringComparison.OrdinalIgnoreCase))
            {
                _input = p_partial(common);
                _moveCaretToEnd = true;
            }

            AddLine(string.Join("   ", p_matches.Take(MaxListedMatches)) +
                    (p_matches.Count > MaxListedMatches ? $"   (+{p_matches.Count - MaxListedMatches} more)" : string.Empty),
                    LineKind.Echo);
            _scrollToBottom = true;
        }

        private void RefreshHint()
        {
            if (_hintSource == _input) return;
            _hintSource = _input;

            int space = _input.IndexOf(' ');
            if (space < 0)
            {
                string typed = _input.Trim();
                IEnumerable<string> commands = DevCommandRegistry.All
                    .Where(c => typed.Length == 0 || c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Name);
                _hint = string.Join("   ", commands.Take(12));
                return;
            }

            if (!DevCommandRegistry.TryGet(_input.Substring(0, space), out DevCommand command))
            {
                _hint = string.Empty;
                return;
            }

            _hint = command.Usage;
            if (!command.HasSuggestions) return;

            string partial = _input.Substring(space + 1).TrimStart().Trim('"');
            List<string> matches = command.GetSuggestions()
                .Where(o => o.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(8)
                .ToList();
            if (matches.Count > 0) _hint += "      " + string.Join("   ", matches);
        }

        private static string Quote(string p_value) => p_value.Contains(' ') ? $"\"{p_value}\"" : p_value;

        private static string CommonPrefix(List<string> p_values)
        {
            string prefix = p_values[0];
            foreach (string value in p_values)
            {
                int length = 0;
                int max = Mathf.Min(prefix.Length, value.Length);
                while (length < max && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(value[length])) length++;
                prefix = prefix.Substring(0, length);
            }
            return prefix;
        }
        #endregion

        #region OUTPUT
        private static void AddLine(string p_text, LineKind p_kind)
        {
            if (p_text == null) return;
            foreach (string line in p_text.Split('\n'))
                _lines.Add((line.TrimEnd('\r'), p_kind));

            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            if (_instance != null) _instance._scrollToBottom = true;
        }

        private static Color ColorFor(LineKind p_kind) => p_kind switch
        {
            LineKind.Echo => new Color(0.6f, 0.65f, 0.7f),
            LineKind.Warning => new Color(1f, 0.85f, 0.35f),
            LineKind.Error => new Color(1f, 0.45f, 0.4f),
            _ => new Color(0.92f, 0.92f, 0.92f),
        };
        #endregion

        #region STYLES
        private void EnsureStyles()
        {
            if (_lineStyle != null) return;

            _panelTexture = MakeTexture(new Color(0.06f, 0.07f, 0.09f, 0.92f));
            _inputTexture = MakeTexture(new Color(0.14f, 0.15f, 0.19f, 1f));

            // Monospace where the OS has one; falls back to the default IMGUI font otherwise.
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New" }, 15);

            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                font = _font,
                fontSize = 15,
                wordWrap = true,
                richText = false,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(2, 2, 1, 1),
            };

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                font = _font,
                fontSize = 16,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 4, 4),
            };
            _inputStyle.normal.background = _inputStyle.focused.background = _inputStyle.hover.background = null;
            _inputStyle.normal.textColor = _inputStyle.focused.textColor = _inputStyle.hover.textColor = Color.white;

            _hintStyle = new GUIStyle(_lineStyle) { fontSize = 13, wordWrap = false, clipping = TextClipping.Clip };
            _hintStyle.normal.textColor = new Color(0.55f, 0.6f, 0.68f);
        }

        private static Texture2D MakeTexture(Color p_color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, p_color);
            texture.Apply();
            return texture;
        }
        #endregion
    }
}
#endif
