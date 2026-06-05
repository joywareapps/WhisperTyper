using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper;

namespace WhisperTyper
{
    public enum ModelLoadState { Unloaded, Loading, Loaded, Failed }
    public enum RecordingState { Idle, Recording, Transcribing, Typing }

    public class WhisperController : IDisposable
    {
        private iMediaFoundation? _mediaFoundation;
        private iModel? _model;
        private Context? _context;

        // NAudio capture
        private WasapiCapture? _wasapiCapture;
        private readonly List<byte> _captureBuffer = new();
        private WaveFormat? _captureFormat;
        private DateTimeOffset _recordingStarted;

        // Streaming: periodic partial transcription during recording
        private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
        private CancellationTokenSource? _partialCts;
        private const int PartialIntervalMs = 4000; // how often to run partial transcription
        private string _partialTypedText = ""; // accumulates filtered text already typed mid-recording

        private ModelLoadState _loadState = ModelLoadState.Unloaded;
        private RecordingState _recordingState = RecordingState.Idle;
        private string _statusMessage = "Ready";

        public event Action<ModelLoadState>? LoadStateChanged;
        public event Action<RecordingState, string>? RecordingStateChanged;
        public event Action<string>? TranscriptionCompleted;
        public event Action<string>? PartialTranscriptionReady;
        public event Action<string>? ErrorOccurred;
        public event Action<string>? DiagnosticLog;

        public ModelLoadState LoadState
        {
            get => _loadState;
            private set { _loadState = value; LoadStateChanged?.Invoke(_loadState); }
        }

        public RecordingState CurrentState => _recordingState;
        public string LoadedModelPath { get; private set; } = "";
        public string LoadedAdapter { get; private set; } = "";
        public bool IsLoadedOnCpu { get; private set; } = false;
        public FillerWordFilter FillerWordFilter { get; } = new();
        public DictionaryService Dictionary { get; } = new();
        public HistoryService History { get; } = new();

        private void SetState(RecordingState state, string message)
        {
            _recordingState = state;
            _statusMessage = message;
            RecordingStateChanged?.Invoke(_recordingState, _statusMessage);
        }

        public WhisperController()
        {
            try { _mediaFoundation = Library.initMediaFoundation(); }
            catch (Exception ex) { ErrorOccurred?.Invoke($"Failed to initialize Media Foundation: {ex.Message}"); }
        }

        public string[] GetGraphicAdapters()
        {
            try { return Library.listGraphicAdapters(); }
            catch (Exception ex) { ErrorOccurred?.Invoke($"Failed to list GPUs: {ex.Message}"); return []; }
        }

        public CaptureDeviceId[] GetMicrophones()
        {
            if (_mediaFoundation == null) return [];
            try { return _mediaFoundation.listCaptureDevices() ?? []; }
            catch (Exception ex) { ErrorOccurred?.Invoke($"Failed to list microphones: {ex.Message}"); return []; }
        }

        public async Task LoadModelAsync(string modelPath, string adapter, eLanguage? language)
        {
            if (!File.Exists(modelPath))
            {
                LoadState = ModelLoadState.Failed;
                SetState(RecordingState.Idle, "Model file not found");
                ErrorOccurred?.Invoke($"Model file not found at: {modelPath}");
                return;
            }

            CleanupModel();
            LoadState = ModelLoadState.Loading;
            SetState(RecordingState.Idle, "Loading model...");

            try
            {
                bool loadedOnCpu = false;
                iModel loadedModel;
                try
                {
                    loadedModel = await Library.loadModelAsync(modelPath, CancellationToken.None,
                        eGpuModelFlags.None, adapter,
                        p => SetState(RecordingState.Idle, $"Loading model on GPU... {p}%"),
                        eModelImplementation.GPU);
                }
                catch (Exception gpuEx)
                {
                    SetState(RecordingState.Idle, "GPU load failed, falling back to CPU...");
                    loadedModel = await Library.loadModelAsync(modelPath, CancellationToken.None,
                        eGpuModelFlags.None, "",
                        p => SetState(RecordingState.Idle, $"Loading model on CPU... {p}%"),
                        eModelImplementation.Reference);
                    loadedOnCpu = true;
                    ErrorOccurred?.Invoke($"GPU load failed: {gpuEx.Message}. Using CPU fallback.");
                }

                _model = loadedModel;
                _context = _model.createContext();
                ConfigureContext(language);

                LoadedModelPath = modelPath;
                LoadedAdapter = loadedOnCpu ? "CPU (Reference)" : adapter;
                IsLoadedOnCpu = loadedOnCpu;
                LoadState = ModelLoadState.Loaded;
                SetState(RecordingState.Idle, IsLoadedOnCpu ? "Ready (CPU Mode)" : "Ready (GPU Mode)");
            }
            catch (Exception ex)
            {
                LoadState = ModelLoadState.Failed;
                SetState(RecordingState.Idle, "Model load failed");
                ErrorOccurred?.Invoke($"Failed to load Whisper model: {ex.Message}");
            }
        }

        public void ConfigureContext(eLanguage? language)
        {
            if (_context == null) return;
            // Only set language when a specific one is chosen; leaving it unset lets Whisper auto-detect.
            if (language.HasValue)
                _context.parameters.language = language.Value;
            _context.parameters.setFlag(eFullParamsFlags.Translate, false);
            _context.parameters.cpuThreads = Math.Max(1, Environment.ProcessorCount / 2);
        }

        public void StartRecording(CaptureDeviceId micDevice)
        {
            if (LoadState != ModelLoadState.Loaded || _context == null)
            {
                ErrorOccurred?.Invoke("Model not loaded"); return;
            }
            if (CurrentState != RecordingState.Idle) return;

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var mmDevice = enumerator.GetDevice(micDevice.endpoint);

                _wasapiCapture = new WasapiCapture(mmDevice);
                _captureFormat = _wasapiCapture.WaveFormat;
                lock (_captureBuffer) _captureBuffer.Clear();

                _wasapiCapture.DataAvailable += (_, e) =>
                {
                    lock (_captureBuffer)
                        _captureBuffer.AddRange(new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded));
                };

                _partialTypedText = "";
                _recordingStarted = DateTimeOffset.Now;
                _wasapiCapture.StartRecording();
                SetState(RecordingState.Recording, "Recording... Speak now");
                DiagnosticLog?.Invoke($"[Capture] WASAPI started on: {micDevice.displayName} ({_captureFormat})");

                _partialCts = new CancellationTokenSource();
                _ = RunPartialTranscriptionLoop(_partialCts.Token);
            }
            catch (Exception ex)
            {
                StopWasapi();
                SetState(RecordingState.Idle, "Recording start failed");
                ErrorOccurred?.Invoke($"Failed to start recording: {ex.Message}");
            }
        }

        public async Task StopRecordingAsync()
        {
            if (CurrentState != RecordingState.Recording) return;

            // Stop the partial loop and wait for any in-flight partial transcription to finish.
            _partialCts?.Cancel();
            _partialCts = null;

            SetState(RecordingState.Transcribing, "Processing audio...");

            byte[] wavBytes = StopWasapiAndGetWav();
            DiagnosticLog?.Invoke($"[Capture] Captured {wavBytes.Length / 1024} KB WAV");

            if (wavBytes.Length == 0)
            {
                SetState(RecordingState.Idle, "No audio captured");
                return;
            }

            try
            {
                // Acquire lock — waits if a partial transcription is still running.
                await _transcriptionLock.WaitAsync();
                string transcription;
                try { transcription = await Task.Run(() => RunFullTranscription(wavBytes)); }
                finally { _transcriptionLock.Release(); }

                transcription = FillerWordFilter.Apply(transcription);
                transcription = Dictionary.Apply(transcription);

                if (!string.IsNullOrWhiteSpace(transcription))
                {
                    History.Add(new HistoryEntry
                    {
                        Text       = transcription,
                        ModelName  = Path.GetFileName(LoadedModelPath),
                        DurationMs = (int)(DateTimeOffset.Now - _recordingStarted).TotalMilliseconds,
                        Language   = LoadedAdapter
                    });

                    // Only type what wasn't already streamed by partial transcriptions.
                    string toType = StripTypedPrefix(transcription, _partialTypedText);
                    _partialTypedText = "";

                    string snippet = transcription.Length > 40 ? transcription[..37] + "..." : transcription;
                    SetState(RecordingState.Typing, $"Typing: \"{snippet}\"");
                    if (!string.IsNullOrWhiteSpace(toType))
                        TranscriptionCompleted?.Invoke(toType);
                    SetState(RecordingState.Idle, $"Done: \"{snippet}\"");
                }
                else
                {
                    SetState(RecordingState.Idle, "No speech detected");
                }
            }
            catch (Exception ex)
            {
                SetState(RecordingState.Idle, "Transcription failed");
                ErrorOccurred?.Invoke($"Transcription error: {ex.Message}");
            }
        }

        private string RunFullTranscription(byte[] wavBytes)
        {
            if (_mediaFoundation == null || _context == null) return "";

            // Write to a temp WAV file so we can use loadAudioFile → iAudioBuffer → runFull.
            string tempPath = Path.Combine(Path.GetTempPath(), $"wt_{Guid.NewGuid():N}.wav");
            try
            {
                File.WriteAllBytes(tempPath, wavBytes);
                DiagnosticLog?.Invoke($"[Transcribe] Temp WAV: {new FileInfo(tempPath).Length / 1024} KB");

                var buffer = _mediaFoundation.loadAudioFile(tempPath, false);
                var callbacks = new MyCallbacks(msg => DiagnosticLog?.Invoke(msg));
                _context.runFull(buffer, callbacks);

                var segs = _context.results(eResultFlags.None).segments;
                DiagnosticLog?.Invoke($"[Transcribe] {segs.Length} segment(s) from context.results()");

                var sb = new StringBuilder();
                foreach (var seg in segs)
                    if (!string.IsNullOrEmpty(seg.text))
                        sb.Append(seg.text);

                string result = sb.ToString().Trim();
                if (string.IsNullOrEmpty(result))
                    result = callbacks.GetText();

                DiagnosticLog?.Invoke($"[Transcribe] Result: \"{result}\"");
                return result;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        // Returns the portion of fullText that comes after the words already covered by typedText,
        // using word-level comparison so punctuation differences between Whisper runs don't cause repeats.
        private static string StripTypedPrefix(string fullText, string typedText)
        {
            if (string.IsNullOrEmpty(typedText)) return fullText;

            // Fast path: exact prefix match.
            if (fullText.StartsWith(typedText, StringComparison.Ordinal))
                return fullText[typedText.Length..].TrimStart();

            // Fuzzy path: compare word-by-word, ignoring punctuation attached to words.
            var typedWords = typedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fullWords  = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int matched = 0;
            for (int i = 0; i < Math.Min(typedWords.Length, fullWords.Length); i++)
            {
                string tw = typedWords[i].Trim('.', '!', '?', ',', ';', ':');
                string fw = fullWords[i].Trim('.', '!', '?', ',', ';', ':');
                if (string.Equals(tw, fw, StringComparison.OrdinalIgnoreCase))
                    matched = i + 1;
                else
                    break;
            }

            return matched == 0
                ? fullText
                : string.Join(" ", fullWords.Skip(matched)).TrimStart();
        }

        // Returns the index just after the last sentence-ending punctuation (. ! ?) in text, or 0 if none.
        private static int FindLastSentenceBoundary(string text)
        {
            for (int i = text.Length - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?')
                {
                    if (i == text.Length - 1 || text[i + 1] == ' ')
                        return i + 1;
                }
            }
            return 0;
        }

        private async Task RunPartialTranscriptionLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PartialIntervalMs, ct); }
                catch (OperationCanceledException) { break; }

                if (ct.IsCancellationRequested) break;

                // Skip this cycle if the context is already busy with a previous partial.
                if (!await _transcriptionLock.WaitAsync(0)) continue;
                try
                {
                    byte[] snapshot = GetWavSnapshot();
                    if (snapshot.Length == 0) continue;
                    DiagnosticLog?.Invoke("[Partial] Running mid-recording transcription...");
                    string partial = RunFullTranscription(snapshot);
                    partial = FillerWordFilter.Apply(partial);
                    partial = Dictionary.Apply(partial);

                    if (string.IsNullOrWhiteSpace(partial)) continue;

                    // Only stream up to the last complete sentence so we don't cut mid-word.
                    int boundary = FindLastSentenceBoundary(partial);
                    if (boundary <= _partialTypedText.Length) continue;

                    string stable = partial[..boundary];
                    string newText = stable[_partialTypedText.Length..].TrimStart();
                    if (string.IsNullOrWhiteSpace(newText)) continue;

                    _partialTypedText = stable;
                    DiagnosticLog?.Invoke($"[Partial] Streaming: \"{newText}\"");
                    PartialTranscriptionReady?.Invoke(newText);
                }
                finally { _transcriptionLock.Release(); }
            }
        }

        // Snapshot the current capture buffer as a WAV without stopping capture.
        private byte[] GetWavSnapshot()
        {
            byte[] raw;
            lock (_captureBuffer) raw = _captureBuffer.ToArray();
            if (raw.Length == 0 || _captureFormat == null) return [];
            var ms = new MemoryStream();
            using (var writer = new WaveFileWriter(ms, _captureFormat))
                writer.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private byte[] StopWasapiAndGetWav()
        {
            _wasapiCapture?.StopRecording();
            _wasapiCapture?.Dispose();
            _wasapiCapture = null;

            byte[] raw;
            lock (_captureBuffer) raw = _captureBuffer.ToArray();

            if (raw.Length == 0 || _captureFormat == null) return [];

            // Write WAV into a MemoryStream; ToArray() works even after the writer disposes the stream.
            var ms = new MemoryStream();
            using (var writer = new WaveFileWriter(ms, _captureFormat))
                writer.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private void StopWasapi()
        {
            _wasapiCapture?.StopRecording();
            _wasapiCapture?.Dispose();
            _wasapiCapture = null;
        }

        private void CleanupModel()
        {
            StopWasapi();
            if (_context is IDisposable d) d.Dispose();
            _context = null;
            _model?.Dispose();
            _model = null;
            LoadState = ModelLoadState.Unloaded;
        }

        public void Dispose()
        {
            CleanupModel();
            _mediaFoundation?.Dispose();
            _mediaFoundation = null;
        }

        private class MyCallbacks : Callbacks
        {
            private readonly StringBuilder _text = new();
            private readonly Action<string>? _log;

            public MyCallbacks(Action<string>? log = null) => _log = log;

            protected override void onNewSegment(Context sender, int countNew)
            {
                var segs = sender.results(eResultFlags.None).segments;
                int start = segs.Length - countNew;
                for (int i = start; i < segs.Length; i++)
                    if (!string.IsNullOrEmpty(segs[i].text))
                        _text.Append(segs[i].text);
                _log?.Invoke($"[Transcribe] onNewSegment +{countNew}");
            }

            public string GetText() => _text.ToString().Trim();
        }
    }
}
