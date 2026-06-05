using System.IO;
using System.Text.Json;

namespace WhisperTyper
{
    public class HistoryEntry
    {
        public Guid           Id          { get; set; } = Guid.NewGuid();
        public DateTimeOffset Timestamp   { get; set; } = DateTimeOffset.Now;
        public string         Text        { get; set; } = "";
        public string         ModelName   { get; set; } = "";
        public int            DurationMs  { get; set; }
        public string         Language    { get; set; } = "";
    }

    public class HistoryService
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WhisperTyper", "history.json");

        private const int MaxEntries = 500;

        public List<HistoryEntry> Entries { get; private set; } = [];

        public event Action? Changed;

        public HistoryService() => Load();

        public void Add(HistoryEntry entry)
        {
            Entries.Insert(0, entry);
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            Save();
            Changed?.Invoke();
        }

        public void Delete(Guid id)
        {
            Entries.RemoveAll(e => e.Id == id);
            Save();
            Changed?.Invoke();
        }

        public void Clear()
        {
            Entries.Clear();
            Save();
            Changed?.Invoke();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_path))
                    Entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? [];
            }
            catch { Entries = []; }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(Entries,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
