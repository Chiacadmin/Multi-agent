// File: Search_Agent1/Models/ProposalPack.cs
using System.Collections.Generic;

namespace Search_Agent1.Models
{
    /// Time window for updates (inclusive strings: YYYY-MM-DD).
    public class Timeframe
    {
        public string Start { get; set; } = ""; // "YYYY-MM-DD"
        public string End { get; set; } = ""; // "YYYY-MM-DD"
    }

    /// For transparency: notable papers that were skipped and why.
    public class SkippedExample
    {
        public string Title { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// Final S6 artifact produced by Agent 1 and consumed by the Writer/Updater.
    public class ProposalPack
    {
        public string ChapterId { get; set; } = "ch1";
        public Timeframe Timeframe { get; set; } = new();
        public List<Proposal> Proposals { get; set; } = new();
        public List<SkippedExample> SkippedExamples { get; set; } = new();
    }
}
