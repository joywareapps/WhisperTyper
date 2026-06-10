using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WhisperTyper
{
    public enum LlmProvider { Ollama, LmStudio }

    public class PostProcessingSettings
    {
        public bool Enabled { get; set; } = false;
        public LlmProvider Provider { get; set; } = LlmProvider.Ollama;
        public string Endpoint { get; set; } = "http://localhost:11434/api/generate";
        public string Model { get; set; } = "llama3";
        public string Prompt { get; set; } = "You are a helpful assistant. Please reformat, correct, and polish the following transcribed text while keeping the original meaning. Output ONLY the polished text without any preamble or conversational filler. Here is the text:\n\n{text}";
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
                else
                {
                    return await ProcessLmStudioAsync(prompt, settings);
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
