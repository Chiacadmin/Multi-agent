// Services/LLM/ILLMClient.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Services.LLM
{
    /// <summary>
    /// Minimal LLM client surface used by agents/adapters.
    /// Add more methods later if you need (embeddings, tool calls, etc.).
    /// </summary>
    public interface ILLMClient
    {
        /// <summary>
        /// Plain text completion with explicit system + user messages.
        /// </summary>
        Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model,
            double temperature = 0.2,
            int maxTokens = 2048,
            CancellationToken ct = default
        );

        /// <summary>
        /// Same as CompleteAsync but the model is instructed to return strict JSON;
        /// this helper should parse JSON into T (or return default if parsing fails).
        /// </summary>
        Task<T?> CompleteJsonAsync<T>(
            string systemPrompt,
            string userPrompt,
            string model,
            double temperature = 0.2,
            int maxTokens = 2048,
            CancellationToken ct = default
        );
    }
}
