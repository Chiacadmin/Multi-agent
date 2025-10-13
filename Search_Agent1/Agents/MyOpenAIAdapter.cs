// Agents/MyOpenAIAdapter.cs
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Services.LLM; // <- this is the important line to see ILLMClient

namespace Agents  // <- you can keep namespace Agents
{
    public sealed class MyOpenAIAdapter : ILLMClient
    {
        private readonly ILogger<MyOpenAIAdapter> _log;

        public MyOpenAIAdapter(ILogger<MyOpenAIAdapter> log)
        {
            _log = log;
        }

        public async Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model,
            double temperature = 0.2,
            int maxTokens = 2048,
            CancellationToken ct = default
        )
        {
            _log.LogWarning("MyOpenAIAdapter.CompleteAsync is using a STUB implementation. Wire your LLM here.");
            await Task.Yield();
            return userPrompt; // stub echo
        }

        public async Task<T?> CompleteJsonAsync<T>(
            string systemPrompt,
            string userPrompt,
            string model,
            double temperature = 0.2,
            int maxTokens = 2048,
            CancellationToken ct = default
        )
        {
            var text = await CompleteAsync(systemPrompt, userPrompt, model, temperature, maxTokens, ct);
            try
            {
                return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to parse JSON from LLM response. Text: {Text}", text);
                return default;
            }
        }
    }
}
