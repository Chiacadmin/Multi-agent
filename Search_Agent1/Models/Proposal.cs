// File: Search_Agent1/Models/Proposal.cs
namespace Search_Agent1.Models
{
    public class Proposal
    {
        public string Section { get; set; } = "";  // e.g., "1.2"
        public string Title { get; set; } = "";
        public string Gist { get; set; } = "";  // 4–6 sentences; last ties back to section
        public string Citation { get; set; } = "";
        public string Link { get; set; } = "";  // Prefer DOI/canonical
        public string WhyHere { get; set; } = "";  // ties to a specific section
        public string? Number { get; set; }        // assigned later by Writer (e.g., "1.2.1")


    }
}

