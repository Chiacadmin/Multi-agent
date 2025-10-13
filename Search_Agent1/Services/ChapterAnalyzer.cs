using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Services
{
    /// <summary>
    /// Very small content analyzer:
    /// - strips code/links
    /// - extracts keywords (uni/bi/tri-grams)
    /// - ranks by frequency (stopwords removed)
    /// - returns query seeds suitable for literature search
    /// </summary>
    public sealed class ChapterAnalyzer
    {
        // Simple stopwords (extend as needed)
        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","the","and","or","but","if","then","else","for","while","of","to","in","on","at","by","with",
            "is","are","was","were","be","been","being","as","it","its","this","that","these","those","from","into",
            "we","you","they","he","she","i","our","your","their","not","can","may","might","will","would","should",
            "could","about","over","under","than","such","via","per","etc","eg","ie"
        };

        private static readonly Regex CleanMd = new(@"
            (`{3}[\s\S]*?`{3})|                 # code fences
            (`[^`]*`)|                           # inline code
            (!?\[[^\]]*\]\([^\)]*\))|           # links/images
            (<[^>]+>)                            # html tags
        ", RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex Tokenize = new(@"[A-Za-z0-9][A-Za-z0-9\-]*", RegexOptions.Compiled);

        public IReadOnlyList<string> GetSeedsFromBody(string markdown, int maxSeeds = 12)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<string>();

            // 0) Scrub markdown noise
            var text = CleanMd.Replace(markdown, " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // 1) Grab Title-Case multiword phrases (often headings/terms)
            var titleCase = Regex.Matches(text, @"\b([A-Z][a-zA-Z0-9]+(?:\s+[A-Z][a-zA-Z0-9]+){1,4})\b")
                                 .Cast<Match>().Select(m => m.Value)
                                 .Where(s => s.Split(' ').Length >= 2)
                                 .ToList();

            // 2) Tokenize and build 1–3 grams with simple stoplist filter
            var toks = Tokenize.Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();
            var grams = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            bool IsStop(string w) => Stop.Contains(w) || w.Length < 3;
            void Add(string g, double w = 1.0)
            {
                g = g.Trim('-', ' ').ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(g)) return;
                if (g.Split(' ').All(IsStop)) return;
                grams[g] = grams.GetValueOrDefault(g) + w;
            }

            for (int i = 0; i < toks.Count; i++)
            {
                var w1 = toks[i]; if (IsStop(w1)) continue;
                Add(w1, 0.5); // unigrams (low weight)
                if (i + 1 < toks.Count)
                {
                    var w2 = toks[i + 1]; if (!IsStop(w2)) Add($"{w1} {w2}", 2.0);
                }
                if (i + 2 < toks.Count)
                {
                    var w2 = toks[i + 1]; var w3 = toks[i + 2];
                    if (!IsStop(w2) && !IsStop(w3)) Add($"{w1} {w2} {w3}", 3.0);
                }
            }

            // 3) Boost phrases that appear Title-Case in the doc (likely proper terms)
            foreach (var tc in titleCase)
            {
                var key = tc.ToLowerInvariant();
                grams[key] = grams.GetValueOrDefault(key) + 4.0;
            }

            // 4) Optional: ToC/topic nudges (works even if scanning a single big file)
            var tocBoost = new[]
            {
        "search algorithms","knowledge representation","neural networks",
        "deep learning","sequence models","transformers","prompt engineering",
        "ethics and alignment","ai as a service","aiot","robotics","autonomous systems",
        "cognitive computing"
    };
            foreach (var t in tocBoost) grams[t] = grams.GetValueOrDefault(t) + 5.0;

            // 5) Rank by score, prefer 2–3 word phrases, dedupe, and build queries
            var ranked = grams.Select(kv => new { kv.Key, kv.Value, Len = kv.Key.Count(c => c == ' ') + 1 })
                              .OrderByDescending(x => x.Value + (x.Len >= 2 ? 1.5 : 0))
                              .ThenByDescending(x => x.Len)
                              .Select(x => x.Key)
                              .Distinct()
                              .ToList();

            var seeds = new List<string>();
            foreach (var p in ranked)
            {
                if (p.Length > 50) continue;                // avoid overly long queries
                if (seeds.Count >= maxSeeds) break;

                // stronger suffix variety
                seeds.Add($"{p} survey 2024");
                seeds.Add($"{p} systematic review 2024");
                seeds.Add($"{p} benchmark 2024");
            }

            return seeds.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxSeeds).ToList();
        }

    }
}
