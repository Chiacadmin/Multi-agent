using System.Text.Json;
using Infrastructure.Mcp;

namespace Services
{
    public record Paper(
        string Title,
        string[] Authors,
        string? Abstract,
        string? Url,
        string? PdfUrl,
        string? Doi,
        string? Venue,
        int? Year,
        string Source
    );

    public interface IPaperSearchTool
    {
        Task<IReadOnlyList<Paper>> SearchAsync(string query, DateTime start, DateTime end, int limit = 25);
        Task<string?> GetPdfUrlAsync(string id);
    }

    public sealed class McpPaperSearchTool : IPaperSearchTool, IDisposable
    {
        private readonly McpClient _client;
        public McpPaperSearchTool(McpClient client) { _client = client; }

        public async Task<IReadOnlyList<Paper>> SearchAsync(string query, DateTime start, DateTime end, int limit = 25)
        {
            var res = await _client.CallToolAsync("search_papers", new
            {
                query,
                start = start.ToString("yyyy-MM-dd"),
                end = end.ToString("yyyy-MM-dd"),
                limit
            });

            var jsonNode = res.Content.FirstOrDefault(c => c.Type == "json")?.Json;
            var doc = JsonSerializer.Serialize(jsonNode);
            using var jdoc = JsonDocument.Parse(doc);
            var items = jdoc.RootElement.GetProperty("items");

            var list = new List<Paper>();
            foreach (var it in items.EnumerateArray())
            {
                list.Add(new Paper(
                    it.GetProperty("title").GetString() ?? "",
                    it.GetProperty("authors").EnumerateArray().Select(a => a.GetString() ?? "").Where(s => s.Length > 0).ToArray(),
                    it.TryGetProperty("abstract", out var abs) ? abs.GetString() : null,
                    it.TryGetProperty("url", out var url) ? url.GetString() : null,
                    it.TryGetProperty("pdfUrl", out var pdf) ? pdf.GetString() : null,
                    it.TryGetProperty("doi", out var doi) ? doi.GetString() : null,
                    it.TryGetProperty("venue", out var ven) ? ven.GetString() : null,
                    it.TryGetProperty("year", out var yr) && yr.ValueKind == JsonValueKind.Number ? yr.GetInt32() : (int?)null,
                    it.GetProperty("source").GetString() ?? "unknown"
                ));
            }
            return list;
        }

        public async Task<string?> GetPdfUrlAsync(string id)
        {
            var res = await _client.CallToolAsync("get_pdf_url", new { id });
            var jsonNode = res.Content.FirstOrDefault(c => c.Type == "json")?.Json;
            var str = JsonSerializer.Serialize(jsonNode);
            using var jdoc = JsonDocument.Parse(str);
            return jdoc.RootElement.TryGetProperty("pdfUrl", out var p) ? p.GetString() : null;
        }

        public void Dispose() { _client.Dispose(); }
    }
}
