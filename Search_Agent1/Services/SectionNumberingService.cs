using System.Collections.Generic;
using System.Linq;
using Search_Agent1.Models;

namespace Search_Agent1.Services
{
    public class SectionNumberingService
    {
        /// <summary>
        /// Assigns 1.2.1, 1.2.2... using existing child counts by parent section (e.g., {"1.2":2})
        /// </summary>
        public void AssignNumbers(List<Proposal> proposals, Dictionary<string,int> existingChildrenBySection)
        {
            var grouped = proposals.GroupBy(p => p.Section);
            foreach (var g in grouped)
            {
                var count = existingChildrenBySection.TryGetValue(g.Key, out var n) ? n : 0;
                foreach (var p in g)
                {
                    count += 1;
                    p.Number = $"{g.Key}.{count}";
                }
            }
        }
    }
}
