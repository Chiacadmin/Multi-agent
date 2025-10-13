// File: Search_Agent1/Services/SearchPapersService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Search_Agent1.Functions;
using Search_Agent1.Models;

namespace Search_Agent1.Services
{
    public class SearchPapersService
    {
        private readonly HttpClient _http;
        private readonly ArxivService _arxiv;

        public SearchPapersService(HttpClient http, ArxivService arxiv)
        {
            _http = http;
            _arxiv = arxiv;
        }

        public async Task<List<Paper>> SearchAsync(string query, DateTime start, DateTime end, int limit = 25)
        {
            var results = new List<Paper>();

            // 1) arXiv
            var arxivItems = await _arxiv.SearchAsync(query, start);

            foreach (var x in arxivItems)
            {
                // Authors: ArxivService returns string[]; convert to what Paper expects.
                // If your Paper.Authors is List<string>, keep ToList(); if it is string[], use the array directly.
                var authorsArray = x.Authors ?? Array.Empty<string>();
                var authorsList = authorsArray.ToList();

                // Year: Published is a string "yyyy-MM-dd" -> parse and extract Year
                int year = 0;
                if (!string.IsNullOrWhiteSpace(x.Published) &&
                    DateTimeOffset.TryParse(x.Published,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var dto))
                {
                    year = dto.Year;
                }

                // PdfUrl: best-effort for arXiv (convert /abs/ID to /pdf/ID.pdf)
                string? pdfUrl = null;
                if (!string.IsNullOrWhiteSpace(x.Link))
                {
                    if (x.Link.Contains("/abs/", StringComparison.OrdinalIgnoreCase))
                    {
                        var idPart = x.Link.Split(new[] { "/abs/" }, StringSplitOptions.None).LastOrDefault();
                        if (!string.IsNullOrWhiteSpace(idPart))
                            pdfUrl = $"https://arxiv.org/pdf/{idPart}.pdf";
                    }
                }

                results.Add(new Paper
                {
                    Title = x.Title ?? string.Empty,
                    // If Paper.Authors is List<string>:
                    Authors = authorsList,
                    // If Paper.Authors is string[] instead, use:
                    // Authors = authorsArray,

                    Venue = "arXiv",
                    Year = year,
                    Doi = null,
                    Url = x.Link ?? string.Empty,
                    PdfUrl = pdfUrl,
                    Abstract = string.Empty,
                    Source = "arXiv"
                });
            }

            // 2) TODO: Add OpenAlex/Crossref here if/when you’re ready, then AddRange(...) to results.

            return DedupeAndRank(results).Take(limit).ToList();
        }

        private static List<Paper> DedupeAndRank(List<Paper> items)
        {
            // Dedup by DOI or by normalized title
            var map = new Dictionary<string, Paper>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in items)
            {
                var key = !string.IsNullOrEmpty(p.Doi)
                    ? $"doi:{p.Doi}"
                    : $"title:{NormalizeTitle(p.Title)}";

                if (!map.TryGetValue(key, out var existing))
                {
                    map[key] = p;
                }
                else
                {
                    // prefer item with DOI / newer year
                    var keepExisting =
                        (!string.IsNullOrEmpty(existing.Doi) && string.IsNullOrEmpty(p.Doi)) ||
                        (existing.Year >= p.Year);

                    map[key] = keepExisting ? existing : p;
                }
            }

            // Ranking: DOI first, then year desc
            return map.Values
                .OrderByDescending(p => !string.IsNullOrEmpty(p.Doi))
                .ThenByDescending(p => p.Year)
                .ToList();
        }

        private static string NormalizeTitle(string t)
            => Regex.Replace((t ?? string.Empty).ToLowerInvariant(), @"\s+", " ").Trim();
    }
}
