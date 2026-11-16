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
    /// added at runtime through <see cref="DevCommandRegistry.Register(string,string,Action{string[]},DevSuggestionProvider,string)"/>.
    /// Type <c>help</c> to list them.
    /// </para>
    /// While typing, a suggestion list for the command name or current argument pops up above the input and the hint
    /// line shows the usage with the current argument highlighted (red when an argument is invalid).
    /// Keys: Enter runs, Tab / Shift+Tab accept and cycle suggestions, Up/Down pick a suggestion (or browse history
    /// when the list is closed; typed text filters the history), Esc hides the list or closes, F1 closes.
    /// While open, keyboard and mouse are disconnected from the Input System so typing never moves the
    /// player or triggers hotkeys; they reconnect when it closes. Created automatically when play mode starts.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class DevConsole : MonoBehaviour
    {
        #region CONSTANTS
        public const KeyCode ToggleKey = KeyCode.F1;

        private const int MaxLines = 500;
        private const int MaxHistory = 200;
        private const int MaxPopupRows = 10;
        private const float HeightFraction = 0.45f;
        private const string InputControlName = "Greeny.DevConsole.Input";
        #endregion

        private enum LineKind { Echo, Info, Warning, Error }

        private static readonly Color HintDim = new(0.55f, 0.6f, 0.68f);
        private static readonly Color HintActive = new(1f, 0.93f, 0.55f);
        private static readonly Color HintError = new(1f, 0.45f, 0.4f);
        private static readonly Color PopupText = new(0.82f, 0.84f, 0.88f);
        private static readonly Color PopupSelected = new(1f, 0.93f, 0.4f);

        #region STATIC STATE
        private static DevConsole _instance;
        private static readonly List<(string text, LineKind kind)> _lines = new();
        // Loaded from EditorPrefs on first use and saved on every command, so it survives play sessions and restarts.
        private static List<string> _history;
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
        private string _historyPrefix = string.Empty; // what was typed when browsing started; filters the history
        private Vector2 _scroll;
        private bool _scrollToBottom;
        private bool _focusInput;
        private bool _moveCaretToEnd;
        private bool _waitForToggleRelease; // the key that opened the console is still held
        private bool _closeOnRelease;       // close once Esc/F1 is released, so the release can't reach gameplay input

        private CursorLockMode _previousLockState;
        private bool _previousCursorVisible;

        private Texture2D _panelTexture;
        private Texture2D _inputTexture;
        private Texture2D _popupTexture;
        private Texture2D _selectedTexture;
        private GUIStyle _lineStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _ghostStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _popupStyle;
        private Font _font;
        #endregion

        #region COMPLETION STATE
        private readonly List<string> _suggestions = new();
        private readonly List<string> _tokens = new(); // complete tokens before the one being typed (command name first)
        private string _suggestSource;  // the input _suggestions were built for
        private int _selected;
        private int _popupFirst;        // first visible popup row
        private int _tokenStart;        // index in _input where the token being completed starts
        private string _partial = string.Empty;
        private bool _partialQuoted;
        private string _dismissedFor;   // Esc hid the list for this exact input
        private string _cycleInput;     // the input as Tab last left it: Tab again cycles through the same list

        private readonly List<(string text, Color color)> _hintSegments = new();
        private string _hintSource;
        private int _hintSelected = -1;
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

        /// <summary>Commands typed in this project, oldest first. Kept in EditorPrefs.</summary>
        public static IReadOnlyList<string> History => LoadedHistory;

        public static void ClearHistory()
        {
            LoadedHistory.Clear();
            SaveHistory();
        }

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
            if (_popupTexture != null) Destroy(_popupTexture);
            if (_selectedTexture != null) Destroy(_selectedTexture);
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

            RefreshSuggestions();
            DrawGhostText(inputRect);

            // Hint line: usage of the command being typed with the current argument highlighted.
            RefreshHint();
            DrawHint(new Rect(pad, height - hintHeight - pad + 2f, width - pad * 2, hintHeight));

            // Last, so it's drawn over the output.
            if (PopupVisible) DrawPopup(e, inputRect, width - pad);

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

            if (_lines.Count == 0) Log("Developer console. Type 'help' for commands. Tab completes, Up/Down for history, Esc closes.");

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
                else if (p_event.keyCode == KeyCode.Escape && PopupVisible)
                {
                    // First Esc only hides the suggestion list (until the input changes).
                    _dismissedFor = _input;
                    _cycleInput = null;
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

            RefreshSuggestions();

            switch (p_event.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit();
                    p_event.Use();
                    break;
                case KeyCode.UpArrow:
                    if (PopupVisible) MoveSelection(-1);
                    else BrowseHistory(-1);
                    p_event.Use();
                    break;
                case KeyCode.DownArrow:
                    if (PopupVisible) MoveSelection(+1);
                    else BrowseHistory(+1);
                    p_event.Use();
                    break;
                case KeyCode.Tab:
                    TabComplete(p_event.shift ? -1 : +1);
                    p_event.Use();
                    break;
                case KeyCode.RightArrow when PopupVisible && CaretAtEnd():
                    // Like accepting the grey preview text.
                    TabComplete(+1);
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
            _cycleInput = null;
            _dismissedFor = null;
            _scrollToBottom = true;
            _focusInput = true;
            if (line.Length == 0) return;

            AddToHistory(line);
            Execute(line);
        }

        private bool CaretAtEnd()
        {
            if (GUI.GetNameOfFocusedControl() != InputControlName) return true;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            return editor.cursorIndex >= _input.Length && editor.selectIndex >= _input.Length;
        }

        private void SetInput(string p_value)
        {
            _input = p_value;
            _moveCaretToEnd = true;
        }
        #endregion

        #region HISTORY
        private static string HistoryKey => "Greeny.DevConsole.History." + Application.dataPath;

        private static List<string> LoadedHistory
        {
            get
            {
                if (_history != null) return _history;
                string saved = EditorPrefs.GetString(HistoryKey, string.Empty);
                _history = saved.Split('\n').Where(l => l.Length > 0).ToList();
                return _history;
            }
        }

        private static void SaveHistory() => EditorPrefs.SetString(HistoryKey, string.Join("\n", LoadedHistory));

        // Newest last. Running a command again moves it to the end instead of adding a duplicate.
        private static void AddToHistory(string p_line)
        {
            List<string> history = LoadedHistory;
            history.Remove(p_line);
            history.Add(p_line);
            if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
            SaveHistory();
        }

        // Up = older, Down = newer. Only entries starting with what was typed before browsing are visited,
        // so "sp" + Up walks through earlier spawn commands.
        private void BrowseHistory(int p_direction)
        {
            List<string> history = LoadedHistory;
            if (history.Count == 0) return;

            if (_historyIndex < 0)
            {
                if (p_direction > 0) return;
                _historyPrefix = _input;
            }

            int index = _historyIndex < 0 ? history.Count : _historyIndex;
            do index += p_direction;
            while (index >= 0 && index < history.Count && !MatchesHistoryPrefix(history[index]));

            if (index < 0) return; // already at the oldest match

            if (index >= history.Count)
            {
                _historyIndex = -1;
                SetInput(_historyPrefix);
                return;
            }

            _historyIndex = index;
            SetInput(history[index]);
        }

        private bool MatchesHistoryPrefix(string p_entry) =>
            _historyPrefix.Length == 0 ||
            (p_entry.StartsWith(_historyPrefix, StringComparison.OrdinalIgnoreCase) &&
             !p_entry.Equals(_historyPrefix, StringComparison.OrdinalIgnoreCase));
        #endregion

        #region SUGGESTIONS
        private bool PopupVisible => _suggestions.Count > 0 && _historyIndex < 0 && _input.Length > 0 && _dismissedFor != _input;

        // Rebuilds the list for the token at the end of the input. Cheap enough per keystroke; cached per input string.
        private void RefreshSuggestions()
        {
            if (_suggestSource == _input) return;
            _suggestSource = _input;

            // Tab cycling keeps the list (and the token start) it began with.
            if (_cycleInput != null && _input == _cycleInput) return;
            _cycleInput = null;

            _suggestions.Clear();
            _selected = 0;
            _popupFirst = 0;
            _tokens.Clear();

            List<string> tokens = DevCommandRegistry.Tokenize(_input, out _partial, out _tokenStart, out _partialQuoted);
            if (_tokenStart < _input.Length) tokens.RemoveAt(tokens.Count - 1); // the token being typed
            _tokens.AddRange(tokens);

            IEnumerable<string> options;
            if (_tokens.Count == 0) options = DevCommandRegistry.All.Select(c => c.Name);
            else if (DevCommandRegistry.TryGet(_tokens[0], out DevCommand command))
                options = command.GetSuggestions(_tokens.Count - 1, _tokens.Skip(1).ToList());
            else return;

            // Prefix matches first, then the ones that contain the text anywhere.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> all = options.ToList();
            foreach (string option in all.Where(o => o.StartsWith(_partial, StringComparison.OrdinalIgnoreCase)))
                if (seen.Add(option)) _suggestions.Add(option);
            foreach (string option in all.Where(o => o.IndexOf(_partial, StringComparison.OrdinalIgnoreCase) > 0))
                if (seen.Add(option)) _suggestions.Add(option);

            // Already typed in full: nothing left to suggest.
            if (_suggestions.Count == 1 && _suggestions[0].Equals(_partial, StringComparison.OrdinalIgnoreCase))
                _suggestions.Clear();
        }

        private void MoveSelection(int p_delta)
        {
            if (_suggestions.Count == 0) return;
            _selected = (_selected + p_delta + _suggestions.Count) % _suggestions.Count;

            // While Tab-cycling the arrows keep replacing the token.
            if (_cycleInput != null && _input == _cycleInput) AcceptSelected(false, true);
        }

        // First Tab inserts the selected suggestion (plus a space when it's the only one, so the next argument's
        // list opens). Further Tabs cycle through the same list, Shift+Tab backwards.
        private void TabComplete(int p_direction)
        {
            bool cycling = _cycleInput != null && _input == _cycleInput && _suggestions.Count > 0;
            if (cycling)
            {
                _selected = (_selected + p_direction + _suggestions.Count) % _suggestions.Count;
                AcceptSelected(false, true);
                return;
            }

            if (!PopupVisible) return;

            if (_suggestions.Count == 1) AcceptSelected(true, false);
            else AcceptSelected(false, true);
        }

        private void AcceptSelected(bool p_addSpace, bool p_keepCycling)
        {
            string value = _suggestions[Mathf.Clamp(_selected, 0, _suggestions.Count - 1)];
            string text = value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

            _historyIndex = -1;
            SetInput(_input.Substring(0, Mathf.Min(_tokenStart, _input.Length)) + text + (p_addSpace ? " " : string.Empty));

            if (p_keepCycling)
            {
                _cycleInput = _input;
                _suggestSource = _input; // keep the current list
            }
            else _cycleInput = null;
        }

        private void DrawPopup(Event p_event, Rect p_inputRect, float p_maxX)
        {
            int rows = Mathf.Min(_suggestions.Count, MaxPopupRows);
            if (_selected < _popupFirst) _popupFirst = _selected;
            if (_selected >= _popupFirst + rows) _popupFirst = _selected - rows + 1;
            _popupFirst = Mathf.Clamp(_popupFirst, 0, Mathf.Max(0, _suggestions.Count - rows));

            float rowHeight = _popupStyle.lineHeight + 4f;
            float textWidth = 0f;
            for (int i = _popupFirst; i < _popupFirst + rows; i++)
                textWidth = Mathf.Max(textWidth, Measure(_popupStyle, _suggestions[i]));

            float popupWidth = textWidth + _popupStyle.padding.horizontal + 10f;
            // Opens under the start of the token being completed, like the chat suggestions in block games.
            float x = p_inputRect.x + _inputStyle.padding.left - _popupStyle.padding.left +
                      Measure(_inputStyle, _input.Substring(0, Mathf.Min(_tokenStart, _input.Length)));
            x = Mathf.Clamp(x, p_inputRect.x, Mathf.Max(p_inputRect.x, p_maxX - popupWidth));
            var popupRect = new Rect(x, p_inputRect.y - rows * rowHeight - 2f, popupWidth, rows * rowHeight);

            GUI.DrawTexture(popupRect, _popupTexture);

            for (int row = 0; row < rows; row++)
            {
                int index = _popupFirst + row;
                var rowRect = new Rect(popupRect.x, popupRect.y + row * rowHeight, popupRect.width, rowHeight);
                bool selected = index == _selected;

                if (selected) GUI.DrawTexture(rowRect, _selectedTexture);
                _popupStyle.normal.textColor = selected ? PopupSelected : PopupText;
                GUI.Label(rowRect, _suggestions[index], _popupStyle);

                if (p_event.type == EventType.MouseDown && p_event.button == 0 && rowRect.Contains(p_event.mousePosition))
                {
                    _selected = index;
                    AcceptSelected(true, false);
                    _focusInput = true;
                    p_event.Use();
                }
            }

            // Scroll position when the list is longer than the popup.
            if (_suggestions.Count > rows)
            {
                float barHeight = popupRect.height * rows / _suggestions.Count;
                float barY = popupRect.y + (popupRect.height - barHeight) * _popupFirst / (_suggestions.Count - rows);
                GUI.DrawTexture(new Rect(popupRect.xMax - 3f, barY, 3f, barHeight), _selectedTexture);
            }

            if (p_event.type == EventType.ScrollWheel && popupRect.Contains(p_event.mousePosition))
            {
                MoveSelection(p_event.delta.y > 0 ? +1 : -1);
                p_event.Use();
            }
        }

        // Grey preview of the rest of the selected suggestion, after the typed text.
        private void DrawGhostText(Rect p_inputRect)
        {
            if (!PopupVisible || _partialQuoted || _cycleInput == _input) return;

            string suggestion = _suggestions[Mathf.Clamp(_selected, 0, _suggestions.Count - 1)];
            if (suggestion.Any(char.IsWhiteSpace) || suggestion.Length <= _partial.Length ||
                !suggestion.StartsWith(_partial, StringComparison.OrdinalIgnoreCase)) return;

            float x = p_inputRect.x + _inputStyle.padding.left + Measure(_inputStyle, _input);
            if (x >= p_inputRect.xMax - 8f) return;
            GUI.Label(new Rect(x, p_inputRect.y, p_inputRect.xMax - x, p_inputRect.height), suggestion.Substring(_partial.Length), _ghostStyle);
        }
        #endregion

        #region HINT
        private void RefreshHint()
        {
            int selected = PopupVisible ? _selected : -1;
            if (_hintSource == _input && _hintSelected == selected) return;
            _hintSource = _input;
            _hintSelected = selected;
            _hintSegments.Clear();

            if (_input.Trim().Length == 0)
            {
                _hintSegments.Add((string.Join("   ", DevCommandRegistry.All.Select(c => c.Name)), HintDim));
                return;
            }

            // Still typing the command name: describe the highlighted (or typed) command.
            if (_tokens.Count == 0)
            {
                string name = selected >= 0 ? _suggestions[selected] : _partial;
                if (DevCommandRegistry.TryGet(name, out DevCommand typed)) AddUsage(typed, -1);
                else if (_suggestions.Count == 0) _hintSegments.Add(($"Unknown command '{_partial}'.", HintError));
                return;
            }

            if (!DevCommandRegistry.TryGet(_tokens[0], out DevCommand command))
            {
                _hintSegments.Add(($"Unknown command '{_tokens[0]}'. Type 'help' for a list.", HintError));
                return;
            }

            int current = _tokens.Count - 1; // index of the argument being typed
            AddUsage(command, current);

            // First invalid argument, the one being typed included.
            for (int i = 1; i <= _tokens.Count; i++)
            {
                bool isPartial = i == _tokens.Count;
                if (isPartial && _partial.Length == 0 && !_partialQuoted) break;

                string raw = isPartial ? _partial : _tokens[i];
                if (command.ValidateArgument(i - 1, raw, out string error))
                    continue;

                _hintSegments.Add(("   " + error, HintError));
                return;
            }

            if (command.Description.Length > 0) _hintSegments.Add(("   " + command.Description, HintDim));
        }

        // "kill [target]" with the argument at p_argIndex highlighted; p_argIndex -1 highlights the name.
        private void AddUsage(DevCommand p_command, int p_argIndex)
        {
            _hintSegments.Add((p_command.Name, p_argIndex < 0 ? HintActive : HintDim));

            int active = p_command.UsageIndexFor(p_argIndex);
            for (int i = 0; i < p_command.UsageArguments.Count; i++)
                _hintSegments.Add((" " + p_command.UsageArguments[i], i == active ? HintActive : HintDim));

            if (p_argIndex < 0 && p_command.Description.Length > 0)
                _hintSegments.Add(("   " + p_command.Description, HintDim));
        }

        private void DrawHint(Rect p_rect)
        {
            GUI.BeginClip(p_rect);
            float x = 0f;
            foreach ((string text, Color color) in _hintSegments)
            {
                float segmentWidth = Measure(_hintStyle, text);
                _hintStyle.normal.textColor = color;
                GUI.Label(new Rect(x, 0f, segmentWidth + 4f, p_rect.height), text, _hintStyle);
                x += segmentWidth;
                if (x > p_rect.width) break;
            }
            GUI.EndClip();
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
            _popupTexture = MakeTexture(new Color(0.02f, 0.02f, 0.03f, 0.95f));
            _selectedTexture = MakeTexture(new Color(0.25f, 0.27f, 0.33f, 1f));

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
                richText = false,
                padding = new RectOffset(8, 8, 4, 4),
            };
            _inputStyle.normal.background = _inputStyle.focused.background = _inputStyle.hover.background = null;
            _inputStyle.normal.textColor = _inputStyle.focused.textColor = _inputStyle.hover.textColor = Color.white;

            _ghostStyle = new GUIStyle(_lineStyle)
            {
                fontSize = _inputStyle.fontSize,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, _inputStyle.padding.top, _inputStyle.padding.bottom),
                clipping = TextClipping.Clip,
            };
            _ghostStyle.normal.textColor = new Color(0.45f, 0.48f, 0.55f);

            _hintStyle = new GUIStyle(_lineStyle) { fontSize = 13, wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 1, 1) };
            _hintStyle.normal.textColor = HintDim;

            _popupStyle = new GUIStyle(_lineStyle)
            {
                fontSize = 15,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(6, 6, 1, 1),
            };
        }

        // Width of the text alone. The "|" keeps trailing spaces from being ignored; padding cancels out.
        private static float Measure(GUIStyle p_style, string p_text) =>
            p_style.CalcSize(new GUIContent(p_text + "|")).x - p_style.CalcSize(new GUIContent("|")).x;

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
