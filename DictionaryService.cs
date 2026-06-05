using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperTyper
{
    public class DictionaryEntry
    {
        public string Trigger     { get; set; } = "";
        public string Replacement { get; set; } = "";
    }

    public class DictionaryService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WhisperTyper", "dictionary.json");

        private record CompiledEntry(Regex Pattern, string Replacement);
        private List<CompiledEntry> _compiled = [];

        public List<DictionaryEntry> Entries { get; private set; } = [];

        public DictionaryService() => Load();

        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                    Entries = JsonSerializer.Deserialize<List<DictionaryEntry>>(File.ReadAllText(_path)) ?? [];
            }
            catch { Entries = []; }
            Compile();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void Compile()
        {
            _compiled = Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Trigger))
                .Select(e => new CompiledEntry(
                    new Regex($@"(?<!\w){Regex.Escape(e.Trigger.Trim())}(?!\w)",
                              RegexOptions.IgnoreCase | RegexOptions.Compiled),
                    e.Replacement))
                .ToList();
        }

        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (var entry in _compiled)
                text = entry.Pattern.Replace(text, m => MatchCase(m.Value, entry.Replacement));
            return text;
        }

        // Preserve casing style of the matched text.
        private static string MatchCase(string matched, string replacement)
        {
            if (string.IsNullOrEmpty(replacement)) return replacement;
            if (matched == matched.ToUpper()) return replacement.ToUpper();
            if (char.IsUpper(matched[0])) return char.ToUpper(replacement[0]) + replacement[1..];
            return replacement;
        }
    }
}
