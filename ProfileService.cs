using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WhisperTyper
{
    public class ProfileService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WhisperTyper", "profiles.json");

        private List<Profile> _profiles = new();

        public ProfileService()
        {
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _profiles = JsonSerializer.Deserialize<List<Profile>>(json) ?? new List<Profile>();
                }
            }
            catch
            {
                _profiles = new List<Profile>();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch
            {
            }
        }

        public List<Profile> GetProfiles()
        {
            if (!_profiles.Any(p => p.Name == "Default (All Other Apps)"))
            {
                _profiles.Insert(0, new Profile { Name = "Default (All Other Apps)", TargetProcess = "*" });
            }
            return _profiles;
        }

        public Profile? GetProfileForProcess(string processName)
        {
            var appProfile = _profiles.FirstOrDefault(p => string.Equals(p.TargetProcess, processName, StringComparison.OrdinalIgnoreCase));
            if (appProfile != null) return appProfile;
            
            return _profiles.FirstOrDefault(p => p.TargetProcess == "*");
        }

        public void AddOrUpdateProfile(Profile profile)
        {
            var existing = _profiles.FirstOrDefault(p => string.Equals(p.TargetProcess, profile.TargetProcess, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _profiles.Remove(existing);
            }
            _profiles.Add(profile);
            Save();
        }

        public void RemoveProfile(string name)
        {
            _profiles.RemoveAll(p => p.Name == name);
            Save();
        }

        public Profile CreateProfileFromCurrent(string name, string processName, AppSettings currentSettings, List<DictionaryEntry> dictionaryEntries)
        {
            Whisper.eLanguage? langOverride = null;
            if (currentSettings.Language != "Auto-Detect" && Enum.TryParse<Whisper.eLanguage>(currentSettings.Language, out var parsedLang))
            {
                langOverride = parsedLang;
            }

            var profile = new Profile
            {
                Name = name,
                TargetProcess = processName,
                Language = langOverride,
                TranslateToEnglish = currentSettings.TranslateToEnglish,
                FillerWordRemovalEnabled = currentSettings.FillerWordRemovalEnabled,
                CustomDictionaryEntries = new List<DictionaryEntry>(dictionaryEntries),
                PostProcessing = currentSettings.PostProcessing?.Clone()
            };
            AddOrUpdateProfile(profile);
            return profile;
        }
    }
}
