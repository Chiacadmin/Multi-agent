using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Agents
{
    public interface ISearchProvider
    {
        Task<JsonElement> SearchAsync(string query, DateTime start, DateTime end, int limit);
        string SourceName { get; }
    }
}
