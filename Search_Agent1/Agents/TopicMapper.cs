using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Agents
{
    public static class TopicMapper
    {
        // quick-&-clean topic extraction from a chapter body
        public static IReadOnlyList<string> ExtractTopics(string body, int maxTopics = 8)
        {
            if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
            var text = Regex.Replace(body, @"[`*_#>\[\]\(\)\-\+\=\|]", " ");
            var tokens = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
                              .Where(w => w.Length >= 3 && !Stop.Contains(w))
                              .ToArray();

            // frequency
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in tokens) freq[w] = (freq.TryGetValue(w, out var c) ? c : 0) + 1;

            // score compound bigrams a bit higher
            var bigrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                var bg = $"{tokens[i]} {tokens[i + 1]}";
                if (bg.Split(' ').Any(t => Stop.Contains(t))) continue;
                bigrams[bg] = (bigrams.TryGetValue(bg, out var c) ? c : 0) + 2;
            }

            var candidates = freq.Select(kv => (kv.Key, kv.Value))
                                 .Concat(bigrams.Select(kv => (kv.Key, kv.Value)))
                                 .OrderByDescending(x => x.Value)
                                 .Select(x => x.Key)
                                 .Distinct()
                                 .Take(maxTopics)
                                 .ToList();
            return candidates;
        }

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","with","that","from","this","into","between","about",
            "what","which","when","were","have","has","had","their","them","they",
            "use","used","using","also","such","more","most","very","much","many",
            "can","will","may","might","into","onto","over","under","where","then",
            "than","these","those","because","within","without","each","other"
        };
    }
}
