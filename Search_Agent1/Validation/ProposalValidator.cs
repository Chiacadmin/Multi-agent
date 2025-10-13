using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Search_Agent1.Models;

namespace Search_Agent1.Validation
{
    public class ProposalValidator
    {
        // DOI matcher (Crossref-style): 10.<4-9 digits>/<suffix>
        // Suffix allows common DOI characters.
        private static readonly Regex DoiRegex = new(
            @"\b10\.\d{4,9}/[-._;()/:A-Z0-9]+\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );

        // Simple sentence counter (period/exclamation/question delimiters)
        private static readonly Regex SentenceRegex = new(
            @"[^.!?]+[.!?]",
            RegexOptions.Compiled
        );

        // "section 1.2" or "Section 2.3.4"
        private static readonly Regex WhyHereSectionRegex = new(
            @"\bsection\s+\d+(?:\.\d+)*\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );

        public List<string> Validate(Proposal p)
        {
            var issues = new List<string>();
            var gist = p.Gist ?? string.Empty;
            var link = p.Link ?? string.Empty;
            var citation = p.Citation ?? string.Empty;
            var whyHere = p.WhyHere ?? string.Empty;

            // 1) Gist length: must be 4–6 sentences
            var sentences = SentenceRegex.Matches(gist).Count;
            if (sentences < 4 || sentences > 6)
                issues.Add("Gist must be 4–6 sentences.");

            // 2) Link must be a URL
            if (!Regex.IsMatch(link, @"^https?://", RegexOptions.IgnoreCase))
                issues.Add("Link must be a URL.");

            // 3) arXiv preprint marking (strict)
            // If the link is arXiv, the citation MUST indicate preprint.
            if (!string.IsNullOrEmpty(link) && link.IndexOf("arxiv.org", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (string.IsNullOrWhiteSpace(citation) ||
                    citation.IndexOf("preprint", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    issues.Add("arXiv sources must be labeled as preprint in the citation.");
                }
            }

            // 4) Prefer DOI/canonical link when a DOI exists in the citation
            // If we can extract a DOI from the citation and the link is not doi.org, suggest DOI link.
            var doiMatch = DoiRegex.Match(citation);
            if (doiMatch.Success)
            {
                var doi = doiMatch.Value;
                var doiUrl = $"https://doi.org/{doi}".Replace("https://doi.org/https://doi.org/", "https://doi.org/");

                // If link is not already a doi.org link (allowing case-insensitive check)
                if (link.IndexOf("doi.org/", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    issues.Add($"Prefer DOI link: {doiUrl}");
                }
            }

            // 5) WhyHere must tie back to a specific section (e.g., 'section 1.2')
            if (string.IsNullOrWhiteSpace(whyHere) || !WhyHereSectionRegex.IsMatch(whyHere))
                issues.Add("WhyHere should tie back to a specific section (e.g., 'section 1.2').");

            return issues;
        }

        public static string PreferCanonicalLink(Paper paper)
        {
            if (!string.IsNullOrEmpty(paper.Doi))
            {
                // Normalize in case Doi already contains a doi URL
                var normalized = paper.Doi.Replace("https://doi.org/", "", StringComparison.OrdinalIgnoreCase);
                return $"https://doi.org/{normalized}";
            }
            return string.IsNullOrEmpty(paper.Url) ? "" : paper.Url;
        }
    }
}
