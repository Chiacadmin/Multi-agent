using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

public static class OpenAlexClient
{
    private static readonly HttpClient http = new HttpClient();

    /// <summary>
    /// Call OpenAlex to search works.
    /// </summary>
    public static async Task<JsonElement> SearchWorksAsync(string query, int limit = 10, int timeoutMs = 8000)
    {
        // optional: include your email to avoid 403
        var mailto = "you@example.com";

        var url = $"https://api.openalex.org/works" +
                  $"?search={Uri.EscapeDataString(query)}" +
                  $"&per-page={Math.Max(1, Math.Min(limit, 25))}" +
                  $"&mailto={Uri.EscapeDataString(mailto)}";

        http.DefaultRequestHeaders.Remove("User-Agent");
        http.DefaultRequestHeaders.Add("User-Agent", "SearchAgent1-Updater/1.0 (+mailto:" + mailto + ")");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var resp = await http.GetAsync(url, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            // Wrap it as { items: [...] }
            var items = new List<Dictionary<string, object?>>();
            foreach (var w in arr.EnumerateArray())
            {
                string? title = TryGetString(w, "title");
                string? year = TryGetInt(w, "publication_year")?.ToString();
                string? doi = TryGetString(w, "doi");
                string? venue = TryGetString(w, "host_venue", "display_name");
                string? urlL = TryGetString(w, "primary_location", "landing_page_url") ?? TryGetString(w, "id");
                string? pdf = TryGetString(w, "primary_location", "pdf_url");

                var entry = new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["year"] = year,
                    ["venue"] = venue,
                    ["doi"] = doi,
                    ["url"] = urlL,
                    ["pdf_url"] = pdf
                };
                items.Add(entry);
            }

            var packed = JsonSerializer.SerializeToUtf8Bytes(new { items });
            using var back = JsonDocument.Parse(packed);
            return back.RootElement.Clone();
        }

        // fallback to empty
        using var empty = JsonDocument.Parse("{\"items\":[]}");
        return empty.RootElement.Clone();
    }

    private static string? TryGetString(JsonElement el, params string[] path)
    {
        JsonElement cur = el;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(p, out cur)) return null;
        }
        if (cur.ValueKind == JsonValueKind.String) return cur.GetString();
        if (cur.ValueKind == JsonValueKind.Number) return cur.ToString();
        return null;
    }

    private static int? TryGetInt(JsonElement el, params string[] path)
    {
        var s = TryGetString(el, path);
        if (int.TryParse(s, out var n)) return n;
        return null;
    }
}
