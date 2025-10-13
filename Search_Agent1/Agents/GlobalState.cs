using System.Collections.Concurrent;

namespace Agents
{
    public sealed class GlobalState
    {
        public readonly ConcurrentDictionary<string, byte> SeenPapers = new(StringComparer.OrdinalIgnoreCase);
        public bool TryAddPaperKey(string? key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) return false;
            return SeenPapers.TryAdd(key, 1);
        }
    }
}
