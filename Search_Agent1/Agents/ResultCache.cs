using System.Collections.Concurrent;

namespace Agents
{
    public sealed class ResultCache
    {
        private readonly ConcurrentDictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);
        public bool TryGet(string seed, out string json) => _json.TryGetValue(N(seed), out json!);
        public void Put(string seed, string json) => _json[N(seed)] = json;
        private static string N(string s) => (s ?? "").Trim().ToLowerInvariant();
    }
}
