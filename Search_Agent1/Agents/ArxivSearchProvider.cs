using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Agents
{
    public sealed class ArxivSearchProvider : ISearchProvider
    {
        private static readonly HttpClient http = new HttpClient();
        private readonly int _timeoutMs;
        public string SourceName => "arXiv";

        public ArxivSearchProvider(int timeoutMs = 6000) { _timeoutMs = Math.Max(1000, timeoutMs); }

        public async Task<JsonElement> SearchAsync(string query, DateTime start, DateTime end, int limit)
        {
            var url = "https://export.arxiv.org/api/query" +
                      $"?search_query=all:{Uri.EscapeDataString(query)}" +
                      $"&start=0&max_results={Math.Max(1, Math.Min(limit, 50))}" +
                      "&sortBy=lastUpdatedDate&sortOrder=descending";

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_timeoutMs));
            var resp = await http.GetAsync(url, cts.Token);
            resp.EnsureSuccessStatusCode();
            var xml = await resp.Content.ReadAsStringAsync(cts.Token);

            var items = AtomToItems(xml, start, end);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { items });
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }

        private static object[] AtomToItems(string xml, DateTime start, DateTime end)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace atom = "http://www.w3.org/2005/Atom";
                var entries = doc.Root?.Elements(atom + "entry") ?? Enumerable.Empty<XElement>();

                return entries.Select(e =>
                {
                    string title = (string?)e.Element(atom + "title") ?? "";
                    string abs = (string?)e.Element(atom + "summary") ?? "";
                    string? published = (string?)e.Element(atom + "published");
                    int? year = null;
                    if (DateTime.TryParse(published, out var dt))
                    {
                        if (dt.Date < start || dt.Date > end) return null;
                        year = dt.Year;
                    }

                    var links = e.Elements(atom + "link").ToList();
                    string? absUrl = links.FirstOrDefault(l =>
                        ((string?)l.Attribute("title")) == "abstract" ||
                        ((string?)l.Attribute("rel")) == "alternate")?.Attribute("href")?.Value;
                    string? pdfUrl = links.FirstOrDefault(l =>
                        ((string?)l.Attribute("type"))?.Contains("pdf") == true)?.Attribute("href")?.Value;

                    string? doi = e.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("doi", StringComparison.OrdinalIgnoreCase))?.Value;

                    return new
                    {
                        title = Normalize(title),
                        @abstract = Normalize(abs),
                        year = year?.ToString(),
                        venue = "arXiv",
                        doi = string.IsNullOrWhiteSpace(doi) ? null : doi,
                        url = string.IsNullOrWhiteSpace(absUrl) ? null : absUrl,
                        pdf_url = string.IsNullOrWhiteSpace(pdfUrl) ? null : pdfUrl,
                        source = "arXiv"
                    };
                })
                .Where(x => x != null)
                .ToArray()!;
            }
            catch
            {
                return Array.Empty<object>();
            }

            static string Normalize(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
        }
    }
}
