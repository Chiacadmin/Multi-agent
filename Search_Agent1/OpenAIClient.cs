// File: OpenAIClient.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
// Adjust these using directives to your project layout:
using Search_Agent1.Functions;   // ArxivService
using Search_Agent1.Models;      // Resource/SearchRequest if you keep them here
using System.Linq;
using System.Reflection;

namespace Search_Agent1
{
    /// <summary>
    /// Lightweight wrapper over OpenAI Chat Completions.
    /// - CompleteJsonAsync: JSON-only completions (e.g., for summarization/formatting).
    /// - RunAsync: tool-only turn that MUST call search_arxiv and return results.
    /// - SummarizePapersAsync: example JSON summarizer.
    ///
    /// Added:
    /// - SystemMcpOrchestratorPrompt / UserChapterWiseRequestTemplate constants
    ///   for use in your orchestration / formatting steps.
    /// </summary>
    public class OpenAIClientWrapper
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        private readonly ArxivService? _arxiv;

        // If you use a different base URL or Azure OpenAI, change this:
        private const string Endpoint = "https://api.openai.com/v1/chat/completions";

        // =========================
        // PROMPT CONSTANTS (NEW)
        // =========================

        /// <summary>
        /// System prompt: AI multi-agent orchestrator that MUST use MCP tools and follow
        /// your chapter-wise, significance, and formatting rules. Use this for your
        /// *formatting/synthesis* pass (not for the tool-routing search step).
        /// Replace {START_DATE}/{END_DATE} at runtime.
        /// </summary>
        public const string SystemMcpOrchestratorPrompt =
@"You are a multi-agent textbook updater. Your job is to read chapter content and, for each chapter, search scholarly sources (arXiv and OpenAlex). If a chapter has no good matches, return an empty result for that chapter. Output MUST be JSON only, with exactly ONE JSON object per chapter (no prose).

TOOLS & SOURCES
- Use the available tools to search: first arXiv, then OpenAlex as a fallback (or vice-versa). Do not fabricate papers or links.
- Respect the date window {START_DATE}–{END_DATE} (inclusive).
- Prefer DOI or official PDF/landing page links. Normalize links when possible.

RULES
1) Work CHAPTER BY CHAPTER. Each chapter is independent.
2) Extract 1–3 significant, relevant papers per chapter (fewer is fine). If none are suitable, produce an empty “papers” array for that chapter.
3) Significance means: highly cited, widely adopted, influential, clearly novel, or benchmark-moving. Avoid filler.
4) De-duplicate GLOBALLY: the same paper must not appear in more than one chapter output.
5) Keep summaries plain-language and concise (undergrad-friendly).

STRICT OUTPUT (one JSON object per chapter)
For each chapter, output EXACTLY this JSON object:
{
  ""chapter_id"": ""string"",               // e.g., ""1"" or ""1.3""
  ""chapter_title"": ""string"",
  ""section_seeds"": [ ""string"", ... ],   // the search seeds you used for this chapter (optional if not used)
  ""papers"": [
    {
      ""title"": ""string"",
      ""authors"": [""string"", ...],
      ""year"": 2024,
      ""venue"": ""string"",                 // ""arXiv"" or journal/venue name
      ""doi"": ""string|null"",
      ""url"": ""string|null"",              // landing/abs URL
      ""pdf_url"": ""string|null"",
      ""why_relevant"": ""1–2 sentence plain summary for this CHAPTER""
    }
  ]
}

IMPORTANT
- If no qualifying papers for a chapter, still output the object with ""papers"": [].
- No preface, no commentary, no code fences—ONLY the JSON object per chapter when you respond for that chapter.
";

        /// <summary>
        /// User prompt template: chapter-wise request, 1–3 papers, date window, formatting + labels.
        /// Replace {START_DATE}/{END_DATE} before use.
        /// </summary>
        public const string UserChapterWiseRequestTemplate =
@"Analyze ONE textbook chapter I pass you and return exactly ONE JSON object for that chapter (no prose). Use arXiv and OpenAlex within {START_DATE}–{END_DATE}. It’s OK if no papers are found—then return ""papers"": [].

For this chapter:
- Extract 1–3 significant papers (arXiv and/or OpenAlex).
- Prefer DOI or official PDF/landing page links.
- De-duplicate globally (assume other chapters will be processed separately).

Respond with the strict JSON object specified by the system message (chapter_id, chapter_title, section_seeds, papers[]).
";

        public OpenAIClientWrapper(
            HttpClient http,
            string apiKey,
            ArxivService? arxiv = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _arxiv = arxiv;
        }

        // -----------------------------
        // 1) JSON-only completions (for summarization or formatting passes)
        // -----------------------------
        public async Task<string> CompleteJsonAsync(
            string systemPrompt,
            string userPrompt,
            string model = "gpt-4o-mini",
            double temperature = 0.3)
        {
            // NOTE:
            // Pass in SystemMcpOrchestratorPrompt (with dates interpolated) as systemPrompt,
            // and UserChapterWiseRequestTemplate (with dates interpolated) as userPrompt
            // when you want the model to write the final chapter-wise blocks.
            var payload = new
            {
                model,
                temperature,
                response_format = new { type = "json_object" }, // Encourage JSON-only if you are expecting structured JSON
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt   }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            // choices[0].message.content
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return "{}";

            var content = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(content) ? "{}" : content!;
        }

        // -----------------------------
        // 2) Tool-calling search (tool-only, forced)
        // -----------------------------
        public async Task<List<Resource>> RunAsync(SearchRequest req)
        {
            // IMPORTANT:
            // Keep this a TOOL-ONLY router so the model MUST call the search tool.
            // Adding prose-first instructions here can suppress tool calls and yield zero results.

            var messages = new object[]
            {
                new {
                    role = "system",
                    content =
                        "You are a tool router for scholarly search. You MUST call the provided tools and MUST NOT answer with prose.\r\nSearch both arXiv and OpenAlex for the given topic within the date window. Return only significant and relevant items.\r\n"
                },
                new {
                    role = "user",
                    content =
                        $"Search arXiv for topic: '{req.Topic}'. " +
                        $"Return only items published between {req.StartDate:yyyy-MM-dd} and {req.EndDate:yyyy-MM-dd}. " +
                        $"Limit results to top {req.Limit} by relevance/significance."
                }
            };

            // Only declare tools you actually support. Here: arXiv only.
            var tools = new object[]
            {
                ToolDefArxiv(),
                ToolDefOpenAlex()
            };

            // Force the model to call the arXiv tool (no free-form output)
            var toolChoice = new
            {
                type = "function",
                @function = new { name = "search_arxiv" }
            };

            var payload = new
            {
                model = "gpt-4o-mini",
                messages,
                tools,
                tool_choice = toolChoice
            };

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return new List<Resource>();

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var messageEl))
                return new List<Resource>();

            // New tool call format
            if (messageEl.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array &&
                toolCalls.GetArrayLength() > 0)
            {
                var fn = toolCalls[0].GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.GetProperty("arguments").GetString() ?? "{}";
                var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argsJson) ?? new();

                return await DispatchToolAsync(name, args, req).ConfigureAwait(false);
            }

            // Legacy function_call format (if model returns it)
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() == "function_call")
            {
                var fc = messageEl.GetProperty("function_call");
                var name = fc.GetProperty("name").GetString() ?? "";
                var argsJson = fc.GetProperty("arguments").GetString() ?? "{}";
                var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argsJson) ?? new();

                return await DispatchToolAsync(name, args, req).ConfigureAwait(false);
            }

            return new List<Resource>();
        }

        private static object ToolDefArxiv()
            => new
            {
                type = "function",
                function = new
                {
                    name = "search_arxiv",
                    description = "Search arXiv for a given topic within a date range and return significant/relevant papers.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            topic = new { type = "string", description = "Search topic / query string" },
                            start = new { type = "string", format = "date", description = "Start date (YYYY-MM-DD)" },
                            end = new { type = "string", format = "date", description = "End date (YYYY-MM-DD)" },
                            limit = new { type = "integer", minimum = 1, maximum = 50, description = "Max results (default 25)" }
                        },
                        required = new[] { "topic", "start", "end" }
                    }
                }
            };

        private static object ToolDefOpenAlex()
    => new
    {
        type = "function",
        function = new
        {
            name = "search_openalex",
            description = "Search OpenAlex for papers by topic and date range.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    topic = new { type = "string", description = "Search query" },
                    start = new { type = "string", format = "date", description = "Start date (YYYY-MM-DD)" },
                    end = new { type = "string", format = "date", description = "End date (YYYY-MM-DD)" },
                    limit = new { type = "integer", minimum = 1, maximum = 50 }
                },
                required = new[] { "topic", "start", "end" }
            }
        }
    };


        private async Task<List<Resource>> DispatchToolAsync(string name, Dictionary<string, string> args, SearchRequest fb)
        {
            // Fallbacks from tool args → request
            var topic = args.TryGetValue("topic", out var t) && !string.IsNullOrWhiteSpace(t) ? t : fb.Topic;

            DateTime start = fb.StartDate;
            DateTime end = fb.EndDate;
            if (args.TryGetValue("start", out var s) && DateTime.TryParse(s, out var sdt)) start = sdt;
            if (args.TryGetValue("end", out var e) && DateTime.TryParse(e, out var edt)) end = edt;

            int limit = fb.Limit;
            if (args.TryGetValue("limit", out var l) && int.TryParse(l, out var lval) && lval > 0 && lval <= 50)
                limit = lval;

            switch (name)
            {
                case "search_arxiv":
                    return await CallArxivAsync(topic, start, end, limit).ConfigureAwait(false);
                case "search_openalex":
                    // Future: plug your OpenAlexService here; for now just return empty
                    return new List<Resource>();

                default:
                    return new List<Resource>();
            }
        }

        private async Task<List<Resource>> CallArxivAsync(string topic, DateTime start, DateTime end, int limit)
        {
            if (_arxiv == null) return new List<Resource>();

            var t = _arxiv.GetType();
            var startStr = start.ToString("yyyy-MM-dd");
            var endStr = end.ToString("yyyy-MM-dd");

            // Candidates (in order of preference)
            var candidates = new (Type[] Sig, object[] Args)[]
            {
                // SearchAsync(string, DateTime, DateTime, int)
                ( new[] { typeof(string), typeof(DateTime), typeof(DateTime), typeof(int) },
                  new object[] { topic, start, end, limit } ),

                // SearchAsync(string, DateTime, DateTime)
                ( new[] { typeof(string), typeof(DateTime), typeof(DateTime) },
                  new object[] { topic, start, end } ),

                // SearchAsync(string, string, string, int)
                ( new[] { typeof(string), typeof(string), typeof(string), typeof(int) },
                  new object[] { topic, startStr, endStr, limit } ),

                // SearchAsync(string, string, string)
                ( new[] { typeof(string), typeof(string), typeof(string) },
                  new object[] { topic, startStr, endStr } )
            };

            foreach (var (sig, callArgs) in candidates)
            {
                var mi = t.GetMethod("SearchAsync", sig, Array.Empty<ParameterModifier>());
                if (mi == null) continue;

                // Invoke async: expect Task<List<Resource>>
                var taskObj = mi.Invoke(_arxiv, callArgs);
                if (taskObj is Task<List<Resource>> typedTask)
                {
                    var results = await typedTask.ConfigureAwait(false);
                    if (limit > 0) results = results.Take(limit).ToList();
                    return results ?? new List<Resource>();
                }

                // If it returned non-generic Task, try to await and read Result via reflection
                if (taskObj is Task genericTask)
                {
                    await genericTask.ConfigureAwait(false);
                    var resultProp = genericTask.GetType().GetProperty("Result");
                    if (resultProp?.GetValue(genericTask) is List<Resource> list)
                    {
                        if (limit > 0) list = list.Take(limit).ToList();
                        return list;
                    }
                }
            }

            // No compatible overload found
            return new List<Resource>();
        }

        // -----------------------------
        // 3) Compatibility helper (unchanged, if still used elsewhere)
        // -----------------------------
        public async Task<string> SummarizePapersAsync(string papersJson)
        {
            var messages = new object[]
            {
                new {
                    role = "system",
                    content =
@"You are an expert AI research assistant. Convert the input paper list into the chapter JSON shape the system prompt specifies:
{ chapter_id, chapter_title, section_seeds, papers[] }.
Keep language plain and concise. Output JSON only, no prose.
"
                },
                new {
                    role = "user",
                    content = papersJson
                }
            };

            var payload = new
            {
                model = "gpt-4o-mini",
                temperature = 0.3,
                response_format = new { type = "json_object" },
                messages
            };

            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return "{}";

            var content = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(content) ? "{}" : content!;
        }
    }

    // -----------------------------
    // Example DTOs (updated)
    // -----------------------------
    public class SearchRequest
    {
        public string Topic { get; set; } = "";
        public DateTime StartDate { get; set; } = DateTime.UtcNow.Date.AddYears(-1);
        public DateTime EndDate { get; set; } = DateTime.UtcNow.Date;
        public int Limit { get; set; } = 25;
    }

    public sealed class Resource
    {
        public string Title { get; set; } = "";
        public string[] Authors { get; set; } = Array.Empty<string>(); // never null
        public string Published { get; set; } = "";                    // formatted date (yyyy-MM-dd)
        public string Source { get; set; } = "";
        public string Link { get; set; } = "";
    }
}
