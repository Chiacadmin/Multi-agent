using System.Collections.Generic;
using System.Text;
using Search_Agent1.Models;

namespace Search_Agent1.Renderers
{
    public class LatexRenderer
    {
        public string Render(List<Proposal> proposals)
        {
            var sb = new StringBuilder();
            foreach (var p in proposals)
            {
                var titleLine = Escape($"{p.Number}\\quad {p.Title}");
                var gist = Escape(p.Gist);
                var link = Escape(p.Link);
                var citation = Escape(p.Citation);
                var why = Escape(p.WhyHere);
                // (inside Render)
                sb.AppendLine(@"\subsubsection*{" + titleLine + "}");
                sb.AppendLine(gist);
                sb.AppendLine($@"\textbf{{Link:}} {link}");
                sb.AppendLine($@"\textbf{{Citation:}} {citation}");
                sb.AppendLine($@"\textbf{{Why here:}} {why}");
                sb.AppendLine();

            }
            return sb.ToString();
        }

        private static string Escape(string? s) => (s ?? "")
            .Replace(@"\", @"\textbackslash ")
            .Replace("{", @"\{")
            .Replace("}", @"\}")
            .Replace("_", @"\_");
    }
}
