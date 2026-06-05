using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace WhisperTyper
{
    public enum ModelStatus { NotDownloaded, Downloading, Downloaded, Loaded }

    public class KnownModel
    {
        public string FileName    { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string SizeLabel   { get; init; } = "";
        public string Notes       { get; init; } = "";

        // Computed at runtime
        public ModelStatus Status     { get; set; } = ModelStatus.NotDownloaded;
        public double      Progress   { get; set; }  // 0–100 during download
        public string      LocalPath  { get; set; } = "";
    }

    public class ModelManager
    {
        private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        public static readonly KnownModel[] Catalog =
        [
            new() { FileName = "ggml-tiny.bin",             DisplayName = "Tiny",              SizeLabel = "75 MB",   Notes = "Fastest, lowest accuracy" },
            new() { FileName = "ggml-tiny.en.bin",          DisplayName = "Tiny (English)",    SizeLabel = "75 MB",   Notes = "English-only" },
            new() { FileName = "ggml-base.bin",             DisplayName = "Base",              SizeLabel = "142 MB",  Notes = "Good for general use" },
            new() { FileName = "ggml-base.en.bin",          DisplayName = "Base (English)",    SizeLabel = "142 MB",  Notes = "English-only" },
            new() { FileName = "ggml-small.bin",            DisplayName = "Small",             SizeLabel = "466 MB",  Notes = "" },
            new() { FileName = "ggml-small.en.bin",         DisplayName = "Small (English)",   SizeLabel = "466 MB",  Notes = "English-only" },
            new() { FileName = "ggml-medium.bin",           DisplayName = "Medium",            SizeLabel = "1.5 GB",  Notes = "" },
            new() { FileName = "ggml-large-v3.bin",         DisplayName = "Large v3",          SizeLabel = "3.1 GB",  Notes = "Best accuracy" },
            new() { FileName = "ggml-large-v3-turbo.bin",   DisplayName = "Large v3 Turbo",    SizeLabel = "1.5 GB",  Notes = "⭐ Recommended" },
        ];

        public string ModelsDirectory { get; set; }
        public event Action<KnownModel>? ModelStatusChanged;

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(2) };

        public ModelManager(string modelsDirectory)
        {
            ModelsDirectory = modelsDirectory;
            Directory.CreateDirectory(modelsDirectory);
            RefreshStatus(null);
        }

        public void RefreshStatus(string? loadedPath)
        {
            foreach (var m in Catalog)
            {
                m.LocalPath = Path.Combine(ModelsDirectory, m.FileName);
                bool exists = File.Exists(m.LocalPath);

                if (!exists)
                    // Also scan the known legacy scan paths
                    m.LocalPath = FindInLegacyPaths(m.FileName) ?? m.LocalPath;

                m.Status = !File.Exists(m.LocalPath) ? ModelStatus.NotDownloaded
                         : m.LocalPath == loadedPath  ? ModelStatus.Loaded
                                                       : ModelStatus.Downloaded;
            }
        }

        private static string? FindInLegacyPaths(string fileName)
        {
            string[] dirs = [@"C:\Tools\whisper\models", @"C:\Program Files\Audacity\openvino-models"];
            foreach (var d in dirs)
            {
                string p = Path.Combine(d, fileName);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public async Task DownloadAsync(KnownModel model, CancellationToken ct, Action<double>? onProgress = null)
        {
            Directory.CreateDirectory(ModelsDirectory);
            model.LocalPath = Path.Combine(ModelsDirectory, model.FileName);
            model.Status    = ModelStatus.Downloading;
            model.Progress  = 0;
            ModelStatusChanged?.Invoke(model);

            string url  = BaseUrl + model.FileName;
            string tmp  = model.LocalPath + ".tmp";

            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? -1;
                await using var src  = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(tmp);

                var buf = new byte[81920];
                long downloaded = 0;
                int  read;

                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dest.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        model.Progress = downloaded * 100.0 / total;
                        onProgress?.Invoke(model.Progress);
                        ModelStatusChanged?.Invoke(model);
                    }
                }

                dest.Close();
                File.Move(tmp, model.LocalPath, overwrite: true);
                model.Status   = ModelStatus.Downloaded;
                model.Progress = 100;
                ModelStatusChanged?.Invoke(model);
            }
            catch
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                model.Status   = ModelStatus.NotDownloaded;
                model.Progress = 0;
                ModelStatusChanged?.Invoke(model);
                throw;
            }
        }

        public void Delete(KnownModel model)
        {
            if (File.Exists(model.LocalPath))
                File.Delete(model.LocalPath);
            model.Status = ModelStatus.NotDownloaded;
            ModelStatusChanged?.Invoke(model);
        }
    }
}
