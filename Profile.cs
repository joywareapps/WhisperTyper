using System;
using System.Collections.Generic;
using Whisper;

namespace WhisperTyper
{
    public class Profile
    {
        public string Name { get; set; } = "New Profile";
        public string TargetProcess { get; set; } = ""; // e.g., "notepad", "slack"
        
        // Settings overrides
        public eLanguage? Language { get; set; }
        public bool? TranslateToEnglish { get; set; }
        public bool? FillerWordRemovalEnabled { get; set; }
        public List<DictionaryEntry>? CustomDictionaryEntries { get; set; }
        public PostProcessingSettings? PostProcessing { get; set; }
        
        public Profile Clone()
        {
            return new Profile
            {
                Name = this.Name,
                TargetProcess = this.TargetProcess,
                Language = this.Language,
                TranslateToEnglish = this.TranslateToEnglish,
                FillerWordRemovalEnabled = this.FillerWordRemovalEnabled,
                CustomDictionaryEntries = this.CustomDictionaryEntries == null ? null : new List<DictionaryEntry>(this.CustomDictionaryEntries),
                PostProcessing = this.PostProcessing?.Clone()
            };
        }
    }
}
