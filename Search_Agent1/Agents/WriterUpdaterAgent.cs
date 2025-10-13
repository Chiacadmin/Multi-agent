using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Search_Agent1.Models;
using Search_Agent1.Renderers;
using Search_Agent1.Services;
using Search_Agent1.Validation;

namespace Search_Agent1.Agents
{
    public class WriterUpdaterAgent
    {
        private readonly SectionNumberingService _numbering;
        private readonly LatexRenderer _latex;
        private readonly MarkdownRenderer _md;
        private readonly ProposalValidator _validator;

        public WriterUpdaterAgent(SectionNumberingService numbering, LatexRenderer latex, MarkdownRenderer md, ProposalValidator validator)
        {
            _numbering = numbering;
            _latex = latex;
            _md = md;
            _validator = validator;
        }

        public Task<(string latex, string markdown, Dictionary<string,string[]> changelog, Dictionary<string,string[]> validation)> ComposeAsync(
            ProposalPack pack,
            Dictionary<string,int> existingChildrenBySection)
        {
            var proposals = pack.Proposals.ToList();
            _numbering.AssignNumbers(proposals, existingChildrenBySection);

            // (inside WriterUpdaterAgent)
            var changelog = proposals
                .GroupBy(p => p.Section)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Number ?? "").ToArray());

            var validation = proposals.ToDictionary(
                p => p.Number ?? "",
                p => _validator.Validate(p).ToArray()
            );

            var latex = _latex.Render(proposals);
            var md = _md.Render(proposals);

            return Task.FromResult((latex, md, changelog, validation));

        }
    }
}
