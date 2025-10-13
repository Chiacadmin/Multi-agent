using System.Collections.Generic;

namespace Search_Agent1.Models
{
    public class Paper
    {
        public string Title { get; set; } = "";
        public List<string> Authors { get; set; } = new();
        public string Venue { get; set; } = "";
        public int Year { get; set; }
        public string? Doi { get; set; }
        public string Url { get; set; } = "";      // Prefer DOI link when available
        public string? PdfUrl { get; set; }
        public string Abstract { get; set; } = "";
        public string Source { get; set; } = "";   // OpenAlex|Crossref|arXiv|S2
    }
}
