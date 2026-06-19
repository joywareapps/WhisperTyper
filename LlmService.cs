using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WhisperTyper
{
    public enum LlmProvider { Default, Ollama, LmStudio }

    public class PostProcessingSettings
    {
        public bool Enabled { get; set; } = false;
        public LlmProvider Provider { get; set; } = LlmProvider.Ollama;
        public string Endpoint { get; set; } = "http://localhost:11434/api/generate";
        public string Model { get; set; } = "llama3";
        public string Prompt { get; set; } = "You are a helpful assistant. Please reformat, correct, and polish the following transcribed text while keeping the original meaning. Output ONLY the polished text without any preamble or conversational filler. Here is the text:\n\n{text}";

        public PostProcessingSettings Clone()
        {
            return new PostProcessingSettings
            {
                Enabled = this.Enabled,
                Provider = this.Provider,
                Endpoint = this.Endpoint,
                Model = this.Model,
                Prompt = this.Prompt
            };
        }

        public PostProcessingSettings Resolve(PostProcessingSettings? defaults)
        {
            if (defaults == null) return this;

            var result = this.Clone();
            if (result.Provider == LlmProvider.Default)
            {
                result.Provider = defaults.Provider;
                result.Endpoint = defaults.Endpoint;
            }
            if (string.IsNullOrWhiteSpace(result.Model) || result.Model == "Default")
            {
                result.Model = defaults.Model;
            }
            if (string.IsNullOrWhiteSpace(result.Prompt))
            {
                result.Prompt = defaults.Prompt;
            }
            return result;
        }
    }

    public class LlmService
    {
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<string> ProcessTextAsync(string text, PostProcessingSettings settings)
        {
            if (!settings.Enabled) return text;

            try
            {
                string prompt = settings.Prompt.Replace("{text}", text);

                if (settings.Provider == LlmProvider.Ollama)
                {
                    return await ProcessOllamaAsync(prompt, settings);
                }
                else if (settings.Provider == LlmProvider.LmStudio)
                {
                    return await ProcessLmStudioAsync(prompt, settings);
                }
                else
                {
                    // Fallback for Default if not resolved
                    return await ProcessOllamaAsync(prompt, settings);
                }
            }
            catch (Exception ex)
            {
                return $"[LLM Error: {ex.Message}]\n{text}";
            }
        }

        private async Task<string> ProcessOllamaAsync(string prompt, PostProcessingSettings settings)
        {
            var requestBody = new
            {
                model = settings.Model,
                prompt = prompt,
                stream = false
            };

            var response = await _httpClient.PostAsync(settings.Endpoint, 
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));
            
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("response").GetString() ?? "";
        }

        private async Task<string> ProcessLmStudioAsync(string prompt, PostProcessingSettings settings)
        {
            var requestBody = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                temperature = 0.7,
                stream = false
            };

            var response = await _httpClient.PostAsync(settings.Endpoint, 
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));
            
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }
}
