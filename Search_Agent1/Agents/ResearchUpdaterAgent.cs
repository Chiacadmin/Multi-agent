// Agents/ResearchUpdaterAgent.cs
// Agent-1 partial implementation focused on S2 (search) via MCP tool

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Services; // IPaperSearchTool, Paper

namespace Agents // <- change to your project's agents namespace if needed
{
    /// <summary>
    /// ResearchUpdaterAgent
    /// - S1: Chapter -> search plan (themes & queries)  [handled elsewhere via LLM]
    /// - S2: Execute searches (THIS FILE: wired to MCP tool)
    /// - S3: Inclusion + mapping                         [LLM step]
    /// - S4: Draft subsections                           [LLM step]
    /// - S5: Dedupe + ordering                           [LLM/logic]
    /// - S6: Proposals pack                              [LLM/logic]
    /// </summary>
    public sealed class ResearchUpdaterAgent
    {
        private readonly IPaperSearchTool _papers;

        // Add your other dependencies here as needed (LLM client, parsers, validators)
        // private readonly ILLMClient _llm;
        // private readonly ChapterParser _chapterParser;
        // private readonly ProposalValidator _validator;
        // etc.

        public ResearchUpdaterAgent(IPaperSearchTool papers /* , ILLMClient llm, ChapterParser chapterParser, ... */)
        {
            _papers = papers;
            // _llm = llm;
            // _chapterParser = chapterParser;
        }

        /// <summary>
        /// S2: Execute searches for each query against the MCP paper tool, returning a deduped set of candidates.
        /// </summary>
        /// <param name="queries">Queries derived from S1 (themes & expansions).</param>
        /// <param name="start">Inclusive timeframe start (UTC date is fine).</param>
        /// <param name="end">Inclusive timeframe end.</param>
        /// <param name="perQuery">Max results per query (server may cap lower).</param>
        public async Task<IReadOnlyList<Paper>> RunS2Async(IEnumerable<string> queries, DateTime start, DateTime end, int perQuery = 20)
        {
            if (queries is null) throw new ArgumentNullException(nameof(queries));
            var queryList = queries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (queryList.Count == 0) return Array.Empty<Paper>();

            var all = new List<Paper>(capacity: queryList.Count * Math.Max(1, perQuery));

            // Execute sequentially (simple & safe). If you need speed, make these Task.WhenAll with a small concurrency limit.
            foreach (var q in queryList)
            {
                var items = await _papers.SearchAsync(q, start, end, perQuery);
                all.AddRange(items);
            }

            // In-agent dedupe: prefer DOI; fallback to normalized title
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Paper>();
            foreach (var p in all)
            {
                var key = !string.IsNullOrWhiteSpace(p.Doi)
                    ? $"doi:{p.Doi!.Trim()}"
                    : $"t:{NormalizeTitle(p.Title)}";

                if (seen.Add(key))
                {
                    result.Add(p);
                }
            }

            // Optional: sort for nicer downstream UX — DOI + peer-reviewed (non-arXiv) first, then recent
            result.Sort((x, y) =>
            {
                int score(Paper p) =>
                    (string.IsNullOrWhiteSpace(p.Doi) ? 0 : 2)
                    + (p.Source == "OpenAlex" && !string.Equals(p.Venue, "arXiv", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Venue) ? 1 : 0)
                    + (p.Year ?? 0);
                return score(y).CompareTo(score(x));
            });

            return result;
        }

        private static string NormalizeTitle(string s) =>
            (s ?? string.Empty).ToLowerInvariant().Replace("\u00A0", " ").Replace("\t", " ").Replace("\n", " ").Replace("\r", " ").Trim();

        // ---------------------------------------------------------------------
        // Example orchestration method (optional) showing where S2 fits.
        // ---------------------------------------------------------------------
        /*
        public async Task<ProposalPack> RunChapterUpdatePipelineAsync(string chapterMarkdown, DateTime start, DateTime end)
        {
            // S1: derive themes & queries (LLM)
            var plan = await _llm.DeriveSearchPlanAsync(chapterMarkdown);

            // S2: search via MCP (this method)
            var candidates = await RunS2Async(plan.GlobalQueries.Concat(plan.ThemeQueries), start, end, perQuery: 25);

            // S3: inclusion & mapping (LLM)
            var mapping = await _llm.ChooseInclusionsAndMapAsync(chapterMarkdown, candidates);

            // S4: draft subsections (LLM)
            var drafts = await _llm.DraftSubsectionsAsync(chapterMarkdown, mapping);

            // S5: dedupe & order
            var refined = ProposalRefiner.MergeAndOrder(drafts);

            // S6: pack + validate
            var pack = new ProposalPack
            {
                ChapterId = plan.ChapterId,
                Timeframe = new Timeframe { Start = start, End = end },
                Proposals = refined,
                SkippedExamples = mapping.SkippedExamples
            };

            // Optionally validate
            // pack = ProposalValidator.Validate(pack);

            return pack;
        }
        */
    }
}
