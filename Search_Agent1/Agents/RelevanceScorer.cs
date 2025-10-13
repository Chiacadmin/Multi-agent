using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Agents
{
    public static class RelevanceScorer
    {
        // Simple, fast scorer in [0..1]
        public static double Score(string seed, string? title, string? abs)
        {
            seed = Norm(seed);
            var terms = seed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(t => t.Length > 2).Distinct().ToArray();
            if (terms.Length == 0) return 0;

            var t = Norm(title ?? "");
            var a = Norm(abs ?? "");
            var all = t + " " + a;

            int hits = 0, titleHits = 0;
            foreach (var term in terms)
            {
                var pat = $@"\b{Regex.Escape(term)}\w*\b";
                if (Regex.IsMatch(all, pat)) hits++;
                if (Regex.IsMatch(t, pat)) titleHits++;
            }

            var recall = (double)hits / terms.Length;      // 0..1
            var titleBoost = Math.Min(0.5, 0.1 * titleHits); // up to +0.5
            return Math.Max(0, Math.Min(1, recall + titleBoost));
        }

        private static string Norm(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}
