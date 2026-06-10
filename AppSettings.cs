using Whisper;

namespace WhisperTyper
{
    public record AppSettings(
        string ModelPath = "",
        string GpuAdapter = "",
        string Language = "Auto-Detect",
        int HotkeyIndex = 0,
        bool FillerWordRemovalEnabled = true,
        string[]? FillerWords = null,
        string ModelsDirectory = "",
        bool FixPeriodSpacing = true,
        bool StartWithWindows = false,
        bool AlwaysCopyToClipboard = false,
        bool TranslateToEnglish = false,
        bool AudioFeedbackEnabled = false,
        PostProcessingSettings? PostProcessing = null);
}
