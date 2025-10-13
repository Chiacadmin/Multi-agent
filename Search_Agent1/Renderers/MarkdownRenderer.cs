using System.Collections.Generic;
using System.Text;
using Search_Agent1.Models;

namespace Search_Agent1.Renderers
{
    public class MarkdownRenderer
    {
        public string Render(List<Proposal> proposals)
        {
            var sb = new StringBuilder();
            foreach (var p in proposals)
            {
                sb.AppendLine($"### {p.Number}  {p.Title}");
                sb.AppendLine();
                sb.AppendLine($"**Gist:** {p.Gist}");
                sb.AppendLine($"**Link:** {p.Link}");
                sb.AppendLine($"**Citation:** {p.Citation}");
                sb.AppendLine($"**Why here:** {p.WhyHere}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
