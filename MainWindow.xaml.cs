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
        private ProfileService _profileService;
        private Profile? _editingProfile;
        private LlmService _llmService = new();
        private PostProcessingSettings _postProcessing = new();
        private PostProcessingSettings? _activeSessionLlm;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isClosing = false;
        private string? _detectedDefaultModel;
        private readonly ObservableCollection<string> _fillerWords = new();
        private ModelManager? _modelManager;
        private readonly Dictionary<KnownModel, CancellationTokenSource> _downloads = new();

        private static readonly string _settingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "WhisperTyper", "settings.json");

        private HotkeyConfig _currentHotkey = HotkeyConfig.Default;

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
            _profileService = new ProfileService();

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

                // If LLM post-processing is enabled, we MUST NOT type partial results
                // as we need to wait for the final text to send to the LLM.
                bool llmEnabled = _activeSessionLlm?.Enabled ?? _postProcessing.Enabled;

                if (IsPartialTypingAllowed() && !llmEnabled)
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
            ApplyHotkey(saved.Hotkey ?? HotkeyConfig.Default);

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

            // Restore startup, clipboard, translate, and audio feedback settings
            ChkStartup.IsChecked = saved.StartWithWindows;
            ChkCopyToClipboard.IsChecked = saved.AlwaysCopyToClipboard;
            ChkTranslate.IsChecked = saved.TranslateToEnglish;
            ChkAudioFeedback.IsChecked = saved.AudioFeedbackEnabled;

            // Restore filler word settings
            InitFillerWords(saved);
            InitDictionary(saved);
            InitHistory();
            InitModelManager(saved);
            InitProfiles();
            InitPostProcessing(saved);
        }

        private void InitPostProcessing(AppSettings saved)
        {
            _postProcessing = saved.PostProcessing ?? new PostProcessingSettings();
            UpdateLlmStatusUI();
        }

        private void UpdateLlmStatusUI()
        {
            TxtLlmStatus.Text = _postProcessing.Enabled 
                ? $"Enabled ({_postProcessing.Provider}: {_postProcessing.Model})" 
                : "Disabled";
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
                    Hotkey: _currentHotkey,
                    FillerWordRemovalEnabled: ChkFillerEnabled.IsChecked == true,
                    FillerWords: [.. _fillerWords],
                    ModelsDirectory: _modelManager?.ModelsDirectory ?? "",
                    FixPeriodSpacing: ChkPeriodSpacing.IsChecked == true,
                    StartWithWindows: ChkStartup.IsChecked == true,
                    AlwaysCopyToClipboard: ChkCopyToClipboard.IsChecked == true,
                    TranslateToEnglish: ChkTranslate.IsChecked == true,
                    AudioFeedbackEnabled: ChkAudioFeedback.IsChecked == true,
                    PostProcessing: _postProcessing);
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s));
            }
            catch { }
        }

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
                    if (settings != null) return settings;
                }
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

            // Get standard supported languages and sort them alphabetically
            var languages = Enum.GetValues(typeof(eLanguage))
                .Cast<eLanguage>()
                .Select(lang => new KeyValuePair<string, eLanguage>(lang.ToString(), lang))
                .OrderBy(kv => kv.Key)
                .ToList();

            foreach (var langKvp in languages)
            {
                ComboLanguage.Items.Add(langKvp);
            }

            ComboLanguage.DisplayMemberPath = "Key";
            ComboLanguage.SelectedIndex = 0; // Default: Auto-Detect
        }

        private void ApplyHotkey(HotkeyConfig hotkey)
        {
            _currentHotkey = hotkey;
            _keyboardHook.HotkeyVirtualCode  = hotkey.VirtualCode;
            _keyboardHook.ModifierVirtualCode = hotkey.ModifierCode;
            _keyboardHook.SwallowHotkey       = hotkey.Swallow;
            BtnHotkey.Content = hotkey.Label;

            TxtHotkeyWarning.Visibility = IsPartialTypingAllowed()
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }

        private void BtnHotkey_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new HotkeyRecorderDialog(_currentHotkey) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result is HotkeyConfig hotkey)
            {
                ApplyHotkey(hotkey);
                LogMessage($"Hotkey changed to: {hotkey.Label}");
                if (_controller?.LoadState == ModelLoadState.Loaded)
                    TxtSubStatus.Text = $"Hold {hotkey.Label} to start typing speech";
                SaveSettings();
            }
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

                        TxtSubStatus.Text = $"Hold {_currentHotkey.Label} to start typing speech";
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
                        if (ChkAudioFeedback.IsChecked == true)
                            System.Media.SystemSounds.Asterisk.Play();
                        LogMessage("Recording started... Speak now.");
                        break;

                    case RecordingState.Transcribing:
                        _pulseStoryboard?.Stop();
                        StatusOuterRing.Opacity = 0;
                        StatusCircle.Fill = _transcribingBrush;
                        TxtStatus.Text = "TRANSCRIBING";
                        if (ChkAudioFeedback.IsChecked == true)
                            System.Media.SystemSounds.Exclamation.Play();
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

                    // Detect active window and apply profile
                    string activeProcess = WindowDetectionUtils.GetActiveProcessName();
                    LogMessage($"Active process: {activeProcess}");
                    var profile = _profileService.GetProfileForProcess(activeProcess);
                    
                    if (profile != null)
                    {
                        LogMessage($"Applying profile: {profile.Name}");
                    }
                    
                    eLanguage? defaultLang = null;
                    if (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> selectedLang && selectedLang.Key != "Auto-Detect")
                        defaultLang = selectedLang.Value;

                    // If we have a profile (including Default), apply it. 
                    // ApplyProfile will reset to base settings if profile is null, which won't happen now for Default.
                    _controller.ApplyProfile(profile, defaultLang, ChkTranslate.IsChecked == true, ChkFillerEnabled.IsChecked == true);
                    
                    // Capture LLM settings for this recording session
                    _activeSessionLlm = profile?.PostProcessing?.Resolve(_postProcessing) ?? _postProcessing;

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
                        bool llmEnabled = _activeSessionLlm?.Enabled ?? _postProcessing.Enabled;
                        await _controller.StopRecordingAsync(llmEnabled);
                        // Reset to default settings after recording
                        Dispatcher.Invoke(() => {
                            eLanguage? defaultLang = null;
                            if (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> selectedLang && selectedLang.Key != "Auto-Detect")
                                defaultLang = selectedLang.Value;
                            _controller.ApplyProfile(null, defaultLang, ChkTranslate.IsChecked == true, ChkFillerEnabled.IsChecked == true);
                        });
                    });
                }
            });
        }

        // ── Profiles ────────────────────────────────────────────────────────

        private void InitProfiles()
        {
            RefreshProfilesList();
        }

        private void RefreshProfilesList()
        {
            ProfilesList.ItemsSource = null;
            ProfilesList.ItemsSource = _profileService.GetProfiles();
        }

        private async void BtnCloneProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_editingProfile != null)
            {
                // SAVE MODE: Just save to the profile we are currently editing
                var saveSettings = new AppSettings(
                    Language: (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> l) ? l.Key : "Auto-Detect",
                    FillerWordRemovalEnabled: ChkFillerEnabled.IsChecked == true,
                    TranslateToEnglish: ChkTranslate.IsChecked == true,
                    PostProcessing: _postProcessing
                );

                _profileService.CreateProfileFromCurrent(_editingProfile.Name, _editingProfile.TargetProcess, saveSettings, _controller.Dictionary.Entries);
                LogMessage($"Saved changes to profile: {_editingProfile.Name}");
                TxtSubStatus.Text = $"Changes saved to {_editingProfile.Name}!";
                RefreshProfilesList();
                ResetEditState();
                await Task.Delay(2000);
                return;
            }

            // CLONE MODE: Standard countdown and window detection for NEW profiles
            BtnCloneProfile.IsEnabled = false;
            string originalSubStatus = TxtSubStatus.Text;
            string originalButtonContent = BtnCloneProfile.Content.ToString()!;

            try
            {
                for (int i = 3; i > 0; i--)
                {
                    string activeProcess = WindowDetectionUtils.GetActiveProcessName();
                    var existing = _profileService.GetProfileForProcess(activeProcess);
                    string action = (existing != null && activeProcess != "WhisperTyper" && existing.TargetProcess != "*") ? "Updating" : "Cloning";
                    
                    BtnCloneProfile.Content = (existing != null && activeProcess != "WhisperTyper" && existing.TargetProcess != "*") 
                        ? $"Update Profile for {activeProcess}..." 
                        : originalButtonContent;

                    TxtSubStatus.Text = $"Switch to the target app... {action} in {i}...";
                    LogMessage($"{action} profile in {i}...");
                    await Task.Delay(1000);
                }

                string finalActiveProcess = WindowDetectionUtils.GetActiveProcessName();
                if (finalActiveProcess == "Unknown" || finalActiveProcess == "WhisperTyper")
                {
                    System.Windows.MessageBox.Show("Could not detect a target application. Please ensure you switched to the app you want to clone settings for.", "WhisperTyper", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string profileName = $"{finalActiveProcess} Profile";
                var currentSettings = new AppSettings(
                    Language: (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> l) ? l.Key : "Auto-Detect",
                    FillerWordRemovalEnabled: ChkFillerEnabled.IsChecked == true,
                    TranslateToEnglish: ChkTranslate.IsChecked == true,
                    PostProcessing: _postProcessing
                );

                _profileService.CreateProfileFromCurrent(profileName, finalActiveProcess, currentSettings, _controller.Dictionary.Entries);
                LogMessage($"Created/Updated profile for: {finalActiveProcess}");
                RefreshProfilesList();
                ExpanderProfiles.IsExpanded = true;
                TxtSubStatus.Text = $"Profile saved for {finalActiveProcess}!";
                await Task.Delay(2000);
            }
            finally
            {
                TxtSubStatus.Text = originalSubStatus;
                BtnCloneProfile.Content = originalButtonContent;
                BtnCloneProfile.IsEnabled = true;
            }
        }

        private void ApplyProfileToUI_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is Profile profile)
            {
                // Restore Language
                string langKey = profile.Language?.ToString() ?? "Auto-Detect";
                if (profile.Language.HasValue && (int)profile.Language.Value == 0) langKey = "Auto-Detect";

                for (int i = 0; i < ComboLanguage.Items.Count; i++)
                {
                    if (ComboLanguage.Items[i] is KeyValuePair<string, eLanguage> kv && kv.Key == langKey)
                    {
                        ComboLanguage.SelectedIndex = i;
                        break;
                    }
                }

                // Restore Translate
                ChkTranslate.IsChecked = profile.TranslateToEnglish ?? false;

                // Restore Filler
                ChkFillerEnabled.IsChecked = profile.FillerWordRemovalEnabled ?? true;

                // Restore Dictionary
                if (profile.CustomDictionaryEntries != null)
                {
                    _controller.Dictionary.Entries.Clear();
                    foreach (var entry in profile.CustomDictionaryEntries)
                        _controller.Dictionary.Entries.Add(new DictionaryEntry { Trigger = entry.Trigger, Replacement = entry.Replacement });
                    
                    _controller.Dictionary.Save();
                    _controller.Dictionary.Compile();
                    DictItemsControl.Items.Refresh();
                }

                // Restore Post-Processing
                if (profile.PostProcessing != null)
                {
                    _postProcessing = profile.PostProcessing.Clone();
                    UpdateLlmStatusUI();
                }

                LogMessage($"Loaded settings from profile: {profile.Name}");
                _editingProfile = profile;
                BtnCloneProfile.Content = $"Save Changes to {profile.Name}";
                BtnCancelEdit.Visibility = Visibility.Visible;

                System.Windows.MessageBox.Show($"Settings from '{profile.Name}' have been applied to the main UI. You can now tweak them and click the Save button above.", "Profile Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCancelEdit_Click(object sender, RoutedEventArgs e)
        {
            ResetEditState();
        }

        private void ResetEditState()
        {
            _editingProfile = null;
            BtnCloneProfile.Content = "Clone Current Settings for Active App";
            BtnCancelEdit.Visibility = Visibility.Collapsed;
            LogMessage("Edit cancelled. Back to normal mode.");
        }

        private void RemoveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is Profile profile)
            {
                if (profile.TargetProcess == "*")
                {
                    System.Windows.MessageBox.Show("The Default profile cannot be deleted.", "WhisperTyper", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _profileService.RemoveProfile(profile.Name);
                RefreshProfilesList();
                LogMessage($"Removed profile: {profile.Name}");
            }
        }

        private void BtnConfigureLlm_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new PostProcessingWindow(_postProcessing);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                _postProcessing = dlg.Settings;
                UpdateLlmStatusUI();
                LogMessage(_postProcessing.Enabled ? "LLM Post-Processing enabled." : "LLM Post-Processing disabled.");
                SaveSettings();

                // Update hotkey warning if LLM status changed
                TxtHotkeyWarning.Visibility = IsPartialTypingAllowed()
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
            }
        }

        private void OnTranscriptionCompleted(string transcription)
        {
            Task.Run(async () => {
                string finalOutput = transcription;
                var llmSettings = _activeSessionLlm ?? _postProcessing;

                if (llmSettings.Enabled)
                {
                    Dispatcher.Invoke(() => TxtSubStatus.Text = "✨ LLM is processing...");
                    finalOutput = await _llmService.ProcessTextAsync(transcription, llmSettings);
                }

                Dispatcher.Invoke(() =>
                {
                    LogMessage($"Final Output: \"{finalOutput}\"");
                    // Simulate typing at the active cursor
                    KeyboardSimulator.SimulateTypeString(finalOutput);

                    // Auto-copy to clipboard if enabled
                    if (ChkCopyToClipboard.IsChecked == true)
                    {
                        try { System.Windows.Clipboard.SetText(finalOutput); }
                        catch { /* clipboard may be locked */ }
                    }

                    // Restore sub-status if it was changed
                    if (llmSettings.Enabled)
                        TxtSubStatus.Text = $"Hold {_currentHotkey.Label} to start typing speech";
                });
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

        private bool IsPartialTypingAllowed()
        {
            if (_keyboardHook.ModifierVirtualCode != 0) return false;
            int k = _keyboardHook.HotkeyVirtualCode;
            // Modifier keys held as primary hotkey also interfere with SendInput
            return k is not (0x11 or 0xA2 or 0xA3   // Ctrl variants
                           or 0x12 or 0xA4 or 0xA5   // Alt variants
                           or 0x10 or 0xA0 or 0xA1   // Shift variants
                           or 0x5B or 0x5C);          // Win L/R
        }

        private void ConfigChanged_TriggerEagerLoad(object sender, SelectionChangedEventArgs e)
        {
            // If model is already loaded, we update parameters on the fly
            if (_controller != null && _controller.LoadState == ModelLoadState.Loaded)
            {
                ApplyContextSettings();
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

        private void ChkTranslate_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool translate = ChkTranslate.IsChecked == true;
            ApplyContextSettings();
            LogMessage(translate
                ? "Translate to English enabled."
                : "Translate to English disabled.");
        }

        private void ChkAudioFeedback_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            LogMessage(ChkAudioFeedback.IsChecked == true
                ? "Audio Feedback enabled."
                : "Audio Feedback disabled.");
        }

        /// <summary>
        /// Pushes current UI settings (language, translate) into the Whisper context.
        /// </summary>
        private void ApplyContextSettings()
        {
            if (_controller == null || _controller.LoadState != ModelLoadState.Loaded) return;
            eLanguage? language = null;
            if (ComboLanguage.SelectedItem is KeyValuePair<string, eLanguage> selectedLang && selectedLang.Key != "Auto-Detect")
                language = selectedLang.Value;
            _controller.ConfigureContext(language, ChkTranslate.IsChecked == true);
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