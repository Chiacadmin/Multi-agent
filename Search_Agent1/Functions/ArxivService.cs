using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Search_Agent1.Functions
{
    public class ArxivService
    {
        private readonly HttpClient _http;
        public ArxivService(HttpClient http) => _http = http;

        public async Task<List<Resource>> SearchAsync(string topic, DateTime cutoff)
        {
            // Use HTTPS and escape the topic
            var url = $"https://export.arxiv.org/api/query?search_query=all:{Uri.EscapeDataString(topic)}&sortBy=lastUpdatedDate&max_results=20";

            var resp = await _http.GetStringAsync(url);
            var doc = XDocument.Parse(resp);
            XNamespace ns = "http://www.w3.org/2005/Atom";

            var resources = new List<Resource>();
            var cutoffUtc = cutoff.Kind == DateTimeKind.Utc ? cutoff : cutoff.ToUniversalTime();

            foreach (var entry in doc.Root?.Elements(ns + "entry") ?? Enumerable.Empty<XElement>())
            {
                // --- Parse updated/published time safely (arXiv uses ISO8601 with 'Z') ---
                var updatedStr = entry.Element(ns + "updated")?.Value
                                 ?? entry.Element(ns + "published")?.Value;

                if (string.IsNullOrWhiteSpace(updatedStr) ||
                    !DateTimeOffset.TryParse(updatedStr,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var updatedDto))
                {
                    // Skip entries with no valid timestamp
                    continue;
                }

                // Filter by cutoff
                if (updatedDto.UtcDateTime < cutoffUtc) continue;

                // --- Extract fields with null-safety ---
                var title = (entry.Element(ns + "title")?.Value ?? string.Empty).Trim();

                // arXiv authors can be 0..n <author><name>...</name></author>
                var authors = (entry.Elements(ns + "author") ?? Enumerable.Empty<XElement>())
                    .Select(a => a.Element(ns + "name")?.Value)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();

                // Never let Authors be null
                if (authors == null || authors.Length == 0)
                    authors = Array.Empty<string>();

                var id = entry.Element(ns + "id")?.Value ?? string.Empty;

                resources.Add(new Resource
                {
                    Title = title,
                    Authors = authors,                                   // string[] (never null)
                    Published = updatedDto.UtcDateTime.ToString("yyyy-MM-dd"),
                    Source = "arXiv",
                    Link = id
                });
            }

            return resources;
        }
    }
}
