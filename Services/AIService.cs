using Microsoft.Extensions.Configuration;
using Packo.Services.Interfaces;
using Packo.Services.RequestObjects;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Packo.Services
{
    public class AIService : IAIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public AIService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
        }

        public async Task<string> GetCompletionAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("OpenAI API key is not configured. Set it via Preferences with key 'OpenAI_ApiKey'.");

            var request = new
            {
                model = "gpt-6-luna",
                reasoning_effort = "none",
                messages = new[]
                {
                    new { role = "system", content = "You are a helpful assistant." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.7
            };

            var json = JsonSerializer.Serialize(request);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"OpenAI error: {error}");
            }

            var responseJson = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<OpenAIResponse>(responseJson);

            return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        }
    }
}
