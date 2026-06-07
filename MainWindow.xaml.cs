using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Whisper;

namespace WhisperTyper
{
    public partial class MainWindow : Window
    {
        private WhisperController _controller;
        private GlobalKeyboardHook _keyboardHook;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isClosing = false;
        private string? _detectedDefaultModel;
        private readonly ObservableCollection<string> _fillerWords = new();
        private ModelManager? _modelManager;
        private readonly Dictionary<KnownModel, CancellationTokenSource> _downloads = new();

        private static readonly string _settingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "WhisperTyper", "settings.json");

        private record AppSettings(
            string ModelPath = "",
            string GpuAdapter = "",
            string Language = "Auto-Detect",
            int HotkeyIndex = 0,
            bool FillerWordRemovalEnabled = true,
            string[]? FillerWords = null,
            string ModelsDirectory = "",
            bool FixPeriodSpacing = true,
            bool StartWithWindows = false,
            bool AlwaysCopyToClipboard = false);

        private record HotkeyOption(string Label, int KeyCode, int ModifierCode = 0, bool Swallow = true);

        // Visual brushes for Status states - fully qualified to avoid ambiguity
        private SolidColorBrush _gpuReadyBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Emerald Green
        private SolidColorBrush _cpuReadyBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)); // Blue
        private SolidColorBrush _loadingBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));   // Orange
        private SolidColorBrush _recordingBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // Red
        private SolidColorBrush _transcribingBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x06, 0xB6, 0xD4)); // Cyan
        private SolidColorBrush _mutedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9A, 0xA8));    // Grey

        // Storyboards for animations
        private Storyboard? _pulseStoryboard;

        public MainWindow()
        {
            InitializeComponent();

            _controller = new WhisperController();
            _keyboardHook = new GlobalKeyboardHook();

            // Set up events from controller
            _controller.LoadStateChanged += OnModelLoadStateChanged;
            _controller.RecordingStateChanged += OnRecordingStateChanged;
            _controller.TranscriptionCompleted += OnTranscriptionCompleted;
            _controller.ErrorOccurred += OnControllerErrorOccurred;
            _controller.DiagnosticLog += msg => Dispatcher.Invoke(() => LogMessage(msg));
            _controller.PartialTranscriptionReady += text => Dispatcher.Invoke(() =>
            {
                string preview = text.Length > 60 ? text[..57] + "..." : text;
                TxtSubStatus.Text = $"🎙 \"{preview}\"";
                KeyboardSimulator.SimulateTypeString(text);
            });

            // Set up events from keyboard hook
            _keyboardHook.HotkeyStateChanged += OnHotkeyStateChanged;

            // Initialize notify icon
            InitNotifyIcon();

            // Load configurations
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateDevices();
            PopulateLanguages();
            PopulateHotkeys();
            ScanForModels();
            SetupAnimations();

            // Try to auto-select and eagerly load the first model
            if (!_keyboardHook.IsInstalled)
            {
                System.Windows.MessageBox.Show("Global keyboard hook failed to install. WhisperTyper will not detect your hotkey. Please restart the application as Administrator.", "Hook Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LogMessage("Warning: Global keyboard hook failed to install. Restart as Admin.");
            }
            else
            {
                LogMessage("Global keyboard hook installed successfully.");
            }

            var saved = LoadSettings();

            // Restore hotkey
            if (saved.HotkeyIndex >= 0 && saved.HotkeyIndex < ComboHotkey.Items.Count)
                ComboHotkey.SelectedIndex = saved.HotkeyIndex;

            // Restore GPU adapter
            if (!string.IsNullOrEmpty(saved.GpuAdapter))
            {
                for (int i = 0; i < ComboGpu.Items.Count; i++)
                    if (ComboGpu.Items[i] as string == saved.GpuAdapter) { ComboGpu.SelectedIndex = i; break; }
            }

            // Restore language
            if (!string.IsNullOrEmpty(saved.Language))
            {
                for (int i = 0; i < ComboLanguage.Items.Count; i++)
                    if (ComboLanguage.Items[i] is KeyValuePair<string, eLanguage> kv && kv.Key == saved.Language)
                        { ComboLanguage.SelectedIndex = i; break; }
            }

            // Restore model path — prefer saved path, fall back to first detected
            string modelToLoad = "";
            if (!string.IsNullOrEmpty(saved.ModelPath) && File.Exists(saved.ModelPath))
            {
                if (!ComboModelPath.Items.Contains(saved.ModelPath))
                    ComboModelPath.Items.Insert(0, saved.ModelPath);
                ComboModelPath.SelectedItem = saved.ModelPath;
                modelToLoad = saved.ModelPath;
                LogMessage($"Restored saved model: {saved.ModelPath}");
            }
            else if (ComboModelPath.Items.Count > 0)
            {
                ComboModelPath.SelectedIndex = 0;
                modelToLoad = ComboModelPath.Text;
            }

            if (!string.IsNullOrEmpty(modelToLoad))
                TriggerEagerModelLoad();
            else
                LogMessage("No local models detected. Please browse for a Whisper GGML Model (.bin) file.");

            // Restore startup and clipboard settings
            ChkStartup.IsChecked = saved.StartWithWindows;
            ChkCopyToClipboard.IsChecked = saved.AlwaysCopyToClipboard;

            // Restore filler word settings
            InitFillerWords(saved);
            InitDictionary(saved);
            InitHistory();
            InitModelManager(saved);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _isClosing = true;
            SaveSettings();
            _notifyIcon?.Dispose();
            _keyboardHook.Dispose();
            _controller.Dispose();
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                var s = new AppSettings(
                    ModelPath: ComboModelPath.Text,
                    GpuAdapter: ComboGpu.SelectedItem as string ?? "",
                    Language: (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> l) ? l.Key : "Auto-Detect",
                    HotkeyIndex: ComboHotkey.SelectedIndex,
                    FillerWordRemovalEnabled: ChkFillerEnabled.IsChecked == true,
                    FillerWords: [.. _fillerWords],
                    ModelsDirectory: _modelManager?.ModelsDirectory ?? "",
                    FixPeriodSpacing: ChkPeriodSpacing.IsChecked == true,
                    StartWithWindows: ChkStartup.IsChecked == true,
                    AlwaysCopyToClipboard: ChkCopyToClipboard.IsChecked == true);
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s));
            }
            catch { }
        }

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            // Try to load application icon or fallback to default
            try
            {
                var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/WhisperTyper;component/App.ico"))?.Stream;
                if (iconStream != null)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
                }
            }
            catch
            {
                // Fallback to system icon
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.Text = "WhisperTyper - Local Voice Typing";
            _notifyIcon.DoubleClick += (s, e) => RestoreWindow();

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open Settings", null, (s, e) => RestoreWindow());
            contextMenu.Items.Add("Exit", null, (s, e) => {
                _isClosing = true;
                Close();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.Visible = true;
        }

        private void SetupAnimations()
        {
            // Set up a pulse animation for recording state
            _pulseStoryboard = new Storyboard();

            // Double animation for scale
            DoubleAnimation scaleXAnim = new DoubleAnimation(1.0, 1.8, new Duration(TimeSpan.FromSeconds(1.2)))
            {
                AutoReverse = false,
                RepeatBehavior = RepeatBehavior.Forever
            };
            DoubleAnimation scaleYAnim = new DoubleAnimation(1.0, 1.8, new Duration(TimeSpan.FromSeconds(1.2)))
            {
                AutoReverse = false,
                RepeatBehavior = RepeatBehavior.Forever
            };
            // Double animation for opacity
            DoubleAnimation opacityAnim = new DoubleAnimation(0.9, 0.0, new Duration(TimeSpan.FromSeconds(1.2)))
            {
                AutoReverse = false,
                RepeatBehavior = RepeatBehavior.Forever
            };

            Storyboard.SetTarget(scaleXAnim, StatusOuterRing);
            Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath("RenderTransform.(ScaleTransform.ScaleX)"));
            
            Storyboard.SetTarget(scaleYAnim, StatusOuterRing);
            Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath("RenderTransform.(ScaleTransform.ScaleY)"));

            Storyboard.SetTarget(opacityAnim, StatusOuterRing);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(System.Windows.Shapes.Ellipse.OpacityProperty));

            _pulseStoryboard.Children.Add(scaleXAnim);
            _pulseStoryboard.Children.Add(scaleYAnim);
            _pulseStoryboard.Children.Add(opacityAnim);
        }

        private void PopulateDevices()
        {
            // Populate GPUs
            ComboGpu.Items.Clear();
            var gpus = _controller.GetGraphicAdapters();
            foreach (var gpu in gpus)
            {
                ComboGpu.Items.Add(gpu);
            }
            if (ComboGpu.Items.Count > 0)
            {
                ComboGpu.SelectedIndex = 0;
            }

            // Populate Microphones
            ComboMic.Items.Clear();
            var mics = _controller.GetMicrophones();
            foreach (var mic in mics)
            {
                ComboMic.Items.Add(mic);
            }
            if (ComboMic.Items.Count > 0)
            {
                ComboMic.SelectedIndex = 0;
            }
        }

        private void PopulateLanguages()
        {
            ComboLanguage.Items.Clear();
            // Add Auto-Detect as index 0
            ComboLanguage.Items.Add(new KeyValuePair<string, eLanguage>("Auto-Detect", (eLanguage)0));

            // Add standard supported languages from enum
            foreach (eLanguage lang in Enum.GetValues(typeof(eLanguage)))
            {
                ComboLanguage.Items.Add(new KeyValuePair<string, eLanguage>(lang.ToString(), lang));
            }
            ComboLanguage.DisplayMemberPath = "Key";
            ComboLanguage.SelectedIndex = 0; // Default: Auto-Detect
        }

        private void PopulateHotkeys()
        {
            ComboHotkey.Items.Clear();
            ComboHotkey.Items.Add(new HotkeyOption("Caps Lock (Recommended)", 0x14));
            ComboHotkey.Items.Add(new HotkeyOption("Scroll Lock",             0x91));
            ComboHotkey.Items.Add(new HotkeyOption("Left Alt",                0xA4));
            ComboHotkey.Items.Add(new HotkeyOption("Left Ctrl",               0xA2));
            ComboHotkey.Items.Add(new HotkeyOption("Ctrl + Win",              0x5B, ModifierCode: 0x11, Swallow: true));
            ComboHotkey.Items.Add(new HotkeyOption("F9",                      0x78, Swallow: false));
            ComboHotkey.Items.Add(new HotkeyOption("F10",                     0x79, Swallow: false));
            ComboHotkey.DisplayMemberPath = "Label";
            ComboHotkey.SelectedIndex = 0;
        }

        private void ScanForModels()
        {
            ComboModelPath.Items.Clear();

            // Scan standard suggestion directories
            string[] scanPaths = new[]
            {
                @"C:\Tools\whisper\models",
                @"C:\Program Files\Audacity\openvino-models"
            };

            List<string> foundModels = new List<string>();

            foreach (var dir in scanPaths)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        var files = Directory.GetFiles(dir, "*ggml*.bin", SearchOption.TopDirectoryOnly);
                        foreach (var file in files)
                        {
                            foundModels.Add(file);
                        }
                    }
                    catch { /* Suppress directory access errors */ }
                }
            }

            // Populate combo
            foreach (var model in foundModels)
            {
                ComboModelPath.Items.Add(model);
            }

            // Keep track of first model if found
            if (foundModels.Count > 0)
            {
                _detectedDefaultModel = foundModels[0];
            }
        }

        private void TriggerEagerModelLoad()
        {
            if (_isClosing) return;

            string modelPath = ComboModelPath.Text;
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            string adapter = ComboGpu.SelectedItem as string ?? "";
            eLanguage? language = null;
            if (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> selectedLang && selectedLang.Key != "Auto-Detect")
                language = selectedLang.Value;

            // Load asynchronously in background
            Task.Run(async () => {
                await _controller.LoadModelAsync(modelPath, adapter, language);
            });
        }

        private void OnModelLoadStateChanged(ModelLoadState state)
        {
            OnModelLoadStateChangedInternal(state, true);
        }

        private void OnModelLoadStateChangedInternal(ModelLoadState state, bool logMessage)
        {
            // Execute on UI Thread
            Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case ModelLoadState.Loading:
                        StatusCircle.Fill = _loadingBrush;
                        StatusBlur.Radius = 8;
                        TxtStatus.Text = "Loading Model...";
                        if (logMessage) LogMessage($"Loading model: {ComboModelPath.Text}");
                        break;

                    case ModelLoadState.Loaded:
                        bool isCpu = _controller.IsLoadedOnCpu;
                        StatusCircle.Fill = isCpu ? _cpuReadyBrush : _gpuReadyBrush;
                        StatusBlur.Radius = 8;

                        string adapterText = _controller.LoadedAdapter;
                        TxtStatus.Text = isCpu ? "Ready (CPU Mode)" : $"Ready ({adapterText})";

                        string hotkeyLabel = (ComboHotkey.SelectedItem as HotkeyOption)?.Label ?? "hotkey";
                        TxtSubStatus.Text = $"Hold {hotkeyLabel} to start typing speech";
                        if (logMessage) LogMessage($"Model loaded successfully on {(isCpu ? "CPU (Reference fallback)" : adapterText)}.");
                        RefreshModelList();
                        break;

                    case ModelLoadState.Unloaded:
                        StatusCircle.Fill = _mutedBrush;
                        StatusBlur.Radius = 0;
                        TxtStatus.Text = "Unloaded";
                        TxtSubStatus.Text = "Select a model file to begin";
                        if (logMessage) LogMessage("Model unloaded.");
                        RefreshModelList();
                        break;

                    case ModelLoadState.Failed:
                        StatusCircle.Fill = _recordingBrush; // Red
                        StatusBlur.Radius = 8;
                        TxtStatus.Text = "Model Load Failed";
                        TxtSubStatus.Text = "Check model path, GPU drivers, or select smaller model";
                        if (logMessage) LogMessage("Model load failed. Check path, GPU drivers, or select a smaller model.");
                        break;
                }
            });
        }

        private void OnRecordingStateChanged(RecordingState state, string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtSubStatus.Text = message;

                switch (state)
                {
                    case RecordingState.Idle:
                        _pulseStoryboard?.Stop();
                        StatusOuterRing.Opacity = 0;
                        // Restore state color
                        OnModelLoadStateChangedInternal(_controller.LoadState, false);
                        LogMessage($"Idle - {message}");
                        break;

                    case RecordingState.Recording:
                        StatusCircle.Fill = _recordingBrush;
                        StatusOuterRing.Stroke = _recordingBrush;
                        _pulseStoryboard?.Begin();
                        TxtStatus.Text = "RECORDING";
                        LogMessage("Recording started... Speak now.");
                        break;

                    case RecordingState.Transcribing:
                        _pulseStoryboard?.Stop();
                        StatusOuterRing.Opacity = 0;
                        StatusCircle.Fill = _transcribingBrush;
                        TxtStatus.Text = "TRANSCRIBING";
                        LogMessage("Recording stopped. Running Whisper transcribing...");
                        break;

                    case RecordingState.Typing:
                        StatusCircle.Fill = _gpuReadyBrush;
                        TxtStatus.Text = "TYPING...";
                        LogMessage("Typing transcription result...");
                        break;
                }
            });
        }

        private void OnHotkeyStateChanged(bool isPressed)
        {
            Dispatcher.Invoke(() =>
            {
                if (isPressed)
                {
                    if (_controller.LoadState != ModelLoadState.Loaded)
                    {
                        TxtSubStatus.Text = "Cannot record: Model not loaded yet";
                        LogMessage("Hotkey pressed: Model not loaded yet.");
                        return;
                    }

                    if (ComboMic.SelectedItem is CaptureDeviceId mic)
                    {
                        LogMessage("Hotkey pressed: Starting capture on: " + mic.displayName);
                        _controller.StartRecording(mic);
                    }
                    else
                    {
                        TxtSubStatus.Text = "Error: No microphone selected";
                        LogMessage("Hotkey pressed: No microphone selected.");
                    }
                }
                else
                {
                    LogMessage("Hotkey released: Stopping capture.");
                    // Stop recording asynchronously
                    Task.Run(async () => {
                        await _controller.StopRecordingAsync();
                    });
                }
            });
        }

        private void OnTranscriptionCompleted(string transcription)
        {
            Dispatcher.Invoke(() =>
            {
                LogMessage($"Transcribed: \"{transcription}\"");
                // Simulate typing at the active cursor
                KeyboardSimulator.SimulateTypeString(transcription);

                // Auto-copy to clipboard if enabled
                if (ChkCopyToClipboard.IsChecked == true)
                {
                    try { System.Windows.Clipboard.SetText(transcription); }
                    catch { /* clipboard may be locked */ }
                }
            });
        }

        private void OnControllerErrorOccurred(string error)
        {
            Dispatcher.Invoke(() =>
            {
                LogMessage($"ERROR: {error}");
                System.Windows.MessageBox.Show(error, "WhisperTyper Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void ComboHotkey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboHotkey.SelectedItem is HotkeyOption opt)
            {
                _keyboardHook.HotkeyVirtualCode  = opt.KeyCode;
                _keyboardHook.ModifierVirtualCode = opt.ModifierCode;
                _keyboardHook.SwallowHotkey       = opt.Swallow;

                LogMessage($"Hotkey changed to: {opt.Label}");

                if (_controller?.LoadState == ModelLoadState.Loaded)
                    TxtSubStatus.Text = $"Hold {opt.Label} to start typing speech";
            }
        }

        private void ConfigChanged_TriggerEagerLoad(object sender, SelectionChangedEventArgs e)
        {
            // If model is already loaded, we update parameters on the fly
            if (_controller != null && _controller.LoadState == ModelLoadState.Loaded)
            {
                eLanguage? language = null;
                if (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> selectedLang && selectedLang.Key != "Auto-Detect")
                    language = selectedLang.Value;
                _controller.ConfigureContext(language);
            }
        }

        private void BtnBrowseModel_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Whisper GGML Models (*.bin)|*.bin|All Files (*.*)|*.*",
                Title = "Select Whisper GGML Model File"
            };

            if (dlg.ShowDialog() == true)
            {
                // Add to model combo and select it
                if (!ComboModelPath.Items.Contains(dlg.FileName))
                {
                    ComboModelPath.Items.Add(dlg.FileName);
                }
                ComboModelPath.SelectedItem = dlg.FileName;
                TriggerEagerModelLoad();
            }
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            TriggerEagerModelLoad();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            _notifyIcon?.ShowBalloonTip(3000, "WhisperTyper Active", "The app is running in the background. Hold your hotkey to type speech.", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void LogMessage(string msg)
        {
            if (Dispatcher.CheckAccess())
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                TxtHistory.AppendText($"[{timestamp}] {msg}\r\n");
                TxtHistory.ScrollToEnd();
            }
            else
            {
                Dispatcher.Invoke(() => LogMessage(msg));
            }
        }

        private void BtnClearPanel_Click(object sender, RoutedEventArgs e)
        {
            if (TabHistory.IsChecked == true)
            {
                if (System.Windows.MessageBox.Show("Clear all history?", "WhisperTyper", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _controller.History.Clear();
                    RefreshHistoryList();
                }
            }
            else
            {
                TxtHistory.Clear();
                LogMessage("Diagnostics cleared.");
            }
        }

        // kept for legacy call in LogMessage
        private void BtnClearHistory_Click(object sender, RoutedEventArgs e) => BtnClearPanel_Click(sender, e);

        private void ComboModelPath_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) TriggerEagerModelLoad();
        }

        // ── Startup & Clipboard Settings ──────────────────────────────────

        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool enable = ChkStartup.IsChecked == true;
            try
            {
                StartupManager.SetStartup(enable);
                LogMessage(enable ? "Start with Windows enabled." : "Start with Windows disabled.");
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to update startup setting: {ex.Message}");
                ChkStartup.IsChecked = !enable;
            }
        }

        private void ChkCopyToClipboard_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            LogMessage(ChkCopyToClipboard.IsChecked == true
                ? "Always Copy to Clipboard enabled."
                : "Always Copy to Clipboard disabled.");
        }

        // ── Filler Word Removal ─────────────────────────────────────────────

        private void InitFillerWords(AppSettings saved)
        {
            var words = saved.FillerWords ?? FillerWordFilter.Defaults;
            foreach (var w in words) _fillerWords.Add(w);
            FillerWordsList.ItemsSource = _fillerWords;
            ChkFillerEnabled.IsChecked = saved.FillerWordRemovalEnabled;
            ApplyFillerSettings();
        }

        private void ApplyFillerSettings()
        {
            _controller.FillerWordFilter.IsEnabled = ChkFillerEnabled.IsChecked == true;
            _controller.FillerWordFilter.SetWords(_fillerWords);
        }

        private void FillerEnabled_Changed(object sender, RoutedEventArgs e) => ApplyFillerSettings();

        private void AddFillerWord_Click(object sender, RoutedEventArgs e) => AddFillerWordFromTextBox();

        private void FillerWordTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) AddFillerWordFromTextBox();
        }

        private void AddFillerWordFromTextBox()
        {
            string word = TxtNewFillerWord.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(word) || _fillerWords.Contains(word)) return;
            _fillerWords.Add(word);
            TxtNewFillerWord.Text = "";
            ApplyFillerSettings();
        }

        private void RemoveFillerWord_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string word)
            {
                _fillerWords.Remove(word);
                ApplyFillerSettings();
            }
        }

        private void ResetFillerWords_Click(object sender, RoutedEventArgs e)
        {
            _fillerWords.Clear();
            foreach (var w in FillerWordFilter.Defaults) _fillerWords.Add(w);
            ApplyFillerSettings();
        }

        // ── Custom Dictionary ───────────────────────────────────────────────

        private void InitDictionary(AppSettings saved)
        {
            _controller.Dictionary.FixPeriodSpacing = saved.FixPeriodSpacing;
            ChkPeriodSpacing.IsChecked = saved.FixPeriodSpacing;
            DictItemsControl.ItemsSource = _controller.Dictionary.Entries;
        }

        private void AddDictEntry_Click(object sender, RoutedEventArgs e)
        {
            _controller.Dictionary.Entries.Add(new DictionaryEntry());
            DictItemsControl.Items.Refresh();
        }

        private void RemoveDictEntry_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is DictionaryEntry entry)
            {
                _controller.Dictionary.Entries.Remove(entry);
                _controller.Dictionary.Save();
                _controller.Dictionary.Compile();
                DictItemsControl.Items.Refresh();
            }
        }

        private void DictEntry_Changed(object sender, RoutedEventArgs e)
        {
            _controller.Dictionary.Save();
            _controller.Dictionary.Compile();
        }

        private void PeriodSpacing_Changed(object sender, RoutedEventArgs e)
        {
            if (_controller is null) return;
            _controller.Dictionary.FixPeriodSpacing = ChkPeriodSpacing.IsChecked == true;
        }

        // ── History ─────────────────────────────────────────────────────────

        private void InitHistory()
        {
            _controller.History.Changed += () => Dispatcher.Invoke(RefreshHistoryList);
            RefreshHistoryList();
        }

        private void RefreshHistoryList()
        {
            string filter = TxtHistorySearch.Text.Trim().ToLowerInvariant();
            var source = string.IsNullOrEmpty(filter)
                ? _controller.History.Entries.ToList()
                : _controller.History.Entries.Where(e => e.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            HistoryList.ItemsSource = source;
        }

        private void TabHistory_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            TabDiag.IsChecked = false;
            PanelHistory.Visibility = Visibility.Visible;
            TxtHistory.Visibility   = Visibility.Collapsed;
        }

        private void TabDiag_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            TabHistory.IsChecked = false;
            PanelHistory.Visibility = Visibility.Collapsed;
            TxtHistory.Visibility   = Visibility.Visible;
        }

        private void BtnSearchHistory_Click(object sender, RoutedEventArgs e)
        {
            TxtHistorySearch.Visibility = TxtHistorySearch.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            if (TxtHistorySearch.Visibility == Visibility.Visible)
                TxtHistorySearch.Focus();
        }

        private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistoryList();

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void HistoryCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HistoryEntry entry)
                System.Windows.Clipboard.SetText(entry.Text);
        }

        private void HistoryRetype_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HistoryEntry entry)
                KeyboardSimulator.SimulateTypeString(entry.Text);
        }

        private void HistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HistoryEntry entry)
            {
                _controller.History.Delete(entry.Id);
                RefreshHistoryList();
            }
        }

        // ── Model Manager ───────────────────────────────────────────────────

        private void InitModelManager(AppSettings saved)
        {
            string dir = !string.IsNullOrEmpty(saved.ModelsDirectory) && Directory.Exists(saved.ModelsDirectory)
                ? saved.ModelsDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhisperTyper", "models");

            _modelManager = new ModelManager(dir);
            _modelManager.ModelStatusChanged += m => Dispatcher.BeginInvoke(RefreshModelList);
            _modelManager.RefreshStatus(_controller.LoadedModelPath);

            ModelCatalogList.ItemsSource = ModelManager.Catalog;
            TxtModelsDir.Text = $"Models stored in: {dir}";
            RefreshModelList();
        }

        private void RefreshModelList()
        {
            _modelManager?.RefreshStatus(_controller.LoadedModelPath);
            ModelCatalogList.Items.Refresh();
        }

        private async void ModelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not KnownModel model) return;
            if (model.Status == ModelStatus.Downloading) { CancelDownload(model); return; }
            if (model.Status == ModelStatus.Downloaded || model.Status == ModelStatus.Loaded) return;

            var cts = new CancellationTokenSource();
            _downloads[model] = cts;
            btn.Content = "Cancel";

            try
            {
                await _modelManager!.DownloadAsync(model, cts.Token);
                LogMessage($"Downloaded: {model.FileName}");
                if (!ComboModelPath.Items.Contains(model.LocalPath))
                    ComboModelPath.Items.Insert(0, model.LocalPath);
            }
            catch (OperationCanceledException) { LogMessage($"Download cancelled: {model.FileName}"); }
            catch (Exception ex)              { LogMessage($"Download failed: {ex.Message}"); }
            finally
            {
                _downloads.Remove(model);
                RefreshModelList();
            }
        }

        private void CancelDownload(KnownModel model)
        {
            if (_downloads.TryGetValue(model, out var cts))
            {
                cts.Cancel();
                _downloads.Remove(model);
            }
        }

        private void ModelLoad_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not KnownModel model) return;
            if (!File.Exists(model.LocalPath)) return;
            if (!ComboModelPath.Items.Contains(model.LocalPath))
                ComboModelPath.Items.Insert(0, model.LocalPath);
            ComboModelPath.Text = model.LocalPath;
            TriggerEagerModelLoad();
        }

        private void ModelDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not KnownModel model) return;
            if (model.Status == ModelStatus.Loaded)
            {
                System.Windows.MessageBox.Show("Cannot delete the currently loaded model.", "WhisperTyper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (System.Windows.MessageBox.Show($"Delete {model.FileName}?", "WhisperTyper", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _modelManager!.Delete(model);
            LogMessage($"Deleted: {model.FileName}");
            RefreshModelList();
        }
    }
}