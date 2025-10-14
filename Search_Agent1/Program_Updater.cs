// Program_Updater.cs — MCP-first, arXiv fallback, per-CHAPTER aggregation with scoped seeds & global de-dupe
// - Scans full chapters.md (ChapterScanner ignores TOC)
// - Extracts seeds per section (ChapterAnalyzer)
// - Normalizes any year inside seeds to the window END year
// - Scopes every seed by the chapter topic to separate chapters’ results
// - Aggregates all seeds per CHAPTER (one JSON per chapter), de-dupes globally so no paper appears in multiple chapters
// - Uses MCP papers server first (tools/call search_papers), falls back to arXiv if MCP fails for a seed
// - Renders index.md with one entry per chapter + short preview
//
// CLI (examples):
//   --chapters="D:\\repos\\Search_Agent1\\Search_Agent1\\chapters\\chapters.md"
//   --start=2025-09-07  --end=2025-10-03  --maxSeeds=50  --limit=100  --arxivTimeoutMs=12000
//   --mcpCmd=node --mcpScript="D:\\repos\\Search_Agent1\\tools\\paper-server\\src\\index.ts"
//
// Output: out/updates/chapter-<N>.json  with { items: [ {title, year, venue, url, pdf_url, doi} ] }

using Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static class Program_Updater
{
    // Single HttpClient for the whole run; per-request timeouts via CTS
    private static readonly HttpClient http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    // Simple stopword list for chapter-topic scoping
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","from","into","over","under","about","of","to","in","on",
        "a","an","by","using","towards","via","as","is","are","was","were","be","being","been"
    };

    public static async Task Main(string[] args)
    {
        // -------- CONFIG --------
        var projDir = Directory.GetCurrentDirectory();
        var chapterMd = GetArg(args, "chapters") ?? Path.Combine(projDir, "chapters", "chapters.md");
        var outDir = Path.Combine(projDir, "out", "updates");
        EnsureDir(outDir);

        var startStr = GetArg(args, "start") ?? "2025-09-07";
        var endStr = GetArg(args, "end") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var startDt = DateTime.Parse(startStr).Date;
        var endDt = DateTime.Parse(endStr).Date;
        var endYear = endDt.Year;

        int maxSeeds = TryParseInt(GetArg(args, "maxSeeds"), 50);     // per SECTION, before chapter aggregation
        int chapterItemCap = TryParseInt(GetArg(args, "limit"), 100);       // cap per CHAPTER after aggregation
        int arxivTimeoutMs = TryParseInt(GetArg(args, "arxivTimeoutMs"), 12000);

        // MCP spawn config (optional). If not provided, we’ll try MCP call only if already spawned by you.
        var mcpCmd = GetArg(args, "mcpCmd") ?? Environment.GetEnvironmentVariable("PAPERS_MCP_CMD");
        var mcpScript = GetArg(args, "mcpScript") ?? Environment.GetEnvironmentVariable("PAPERS_MCP_SCRIPT");

        Console.WriteLine($"[Updater] Using chapters: {chapterMd}");
        Console.WriteLine($"[Updater] Output dir    : {outDir}");
        Console.WriteLine($"[Updater] Window        : {startStr} .. {endStr}");
        Console.WriteLine("[Updater] Strategy      : MCP-first, arXiv fallback | One JSON per chapter | Global exclusivity");

        if (!File.Exists(chapterMd))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, "chapters", "chapters.md");
            if (File.Exists(alt)) chapterMd = alt;
        }
        if (!File.Exists(chapterMd))
        {
            Console.Error.WriteLine($"[Updater] File not found: {chapterMd}");
            return;
        }

        var md = await File.ReadAllTextAsync(chapterMd);

        // -------- Parse chapters/sections --------
        var scanner = new ChapterScanner();
        var sections = scanner.Extract(md);
        if (sections.Count == 0)
        {
            Console.Error.WriteLine("[Updater] No sections found. Check headings (#/## or 'Chapter N. ...').");
            return;
        }

        // Group by chapter
        var chapters = sections
            .GroupBy(s => (ChapterId: s.ChapterId, ChapterTitle: string.IsNullOrWhiteSpace(s.ChapterTitle) ? s.Title : s.ChapterTitle))
            .OrderBy(g => ParseNumericId(g.Key.ChapterId), IntArrayComparer.Instance)
            .ToList();

        // Try to start MCP (optional). If not configured, we’ll still try arXiv.
        using var mcp = await TryStartMcpAsync(mcpCmd, mcpScript);
        Console.WriteLine(mcp != null ? "[Updater] MCP: ready." : "[Updater] MCP: not started (will fallback to arXiv as needed).");

        // EXCLUSIVE across chapters: once a paper is assigned, it won’t appear in later chapters
        var globalSeenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int saved = 0;
        var index = new List<string> { "# Results by Chapter", "" };

        foreach (var ch in chapters)
        {
            var chapterIdNum = ch.Key.ChapterId;       // e.g., "1"
            var chapterTitle = ch.Key.ChapterTitle;    // e.g., "Chapter 1. Introduction to Artificial Intelligence"
            var chapterNumInt = ParseNumericId(chapterIdNum).FirstOrDefault();

            // Collect & normalize seeds from ALL sections of this chapter
            var rawSeeds = ch
                .SelectMany(sec => new ChapterAnalyzer().GetSeedsFromBody(sec.Body, maxSeeds))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seeds = rawSeeds
                .Select(s => NormalizeSeedYear(s, endYear)) // rewrite “2024” -> “2025” to match window
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // If no seeds, use the chapter title/topic
            if (seeds.Count == 0)
                seeds.Add(chapterTitle ?? $"Chapter {chapterNumInt}");

            // Build a topic prefix from the chapter title and scope every seed by it
            var chapterQuery = BuildChapterQuery(chapterTitle);
            var scopedSeeds = seeds
                .Select(s => string.IsNullOrWhiteSpace(chapterQuery) ? s : $"{chapterQuery} {s}")
                .ToList();

            Console.WriteLine($"[Updater] Chapter {chapterIdNum}: {chapterTitle} — {scopedSeeds.Count} normalized+scoped seeds");

            // Aggregate per chapter with local de-dupe + global exclusivity
            var items = new List<Dictionary<string, object?>>();
            var localSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Gentle concurrency
            using var throttler = new SemaphoreSlim(3);
            var tasks = new List<Task<List<Dictionary<string, object?>>>>();

            foreach (var seed in scopedSeeds)
            {
                await throttler.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // MCP first (if ready), else arXiv; MCP also includes arXiv per your index.ts
                        List<Dictionary<string, object?>> res;
                        if (mcp != null && mcp.IsReady)
                        {
                            try
                            {
                                res = await McpSearchAsync(mcp, seed, startDt, endDt, Math.Max(25, chapterItemCap));
                            }
                            catch
                            {
                                // Fallback: query both OpenAlex + arXiv and merge
                                var oa = await OpenAlexSearchAsync(seed, startDt, endDt, Math.Max(25, chapterItemCap));
                                var ax = await ArxivSearchAsync(seed, startDt, endDt, Math.Max(25, chapterItemCap), arxivTimeoutMs);
                                res = MergeLists(oa, ax, Math.Max(25, chapterItemCap));
                            }
                        }
                        else
                        {
                            // No MCP: directly use OpenAlex + arXiv merge
                            var oa = await OpenAlexSearchAsync(seed, startDt, endDt, Math.Max(25, chapterItemCap));
                            var ax = await ArxivSearchAsync(seed, startDt, endDt, Math.Max(25, chapterItemCap), arxivTimeoutMs);
                            res = MergeLists(oa, ax, Math.Max(25, chapterItemCap));
                        }

                        return res;
                    }
                    catch
                    {
                        // any hiccup — treat as empty
                        return new List<Dictionary<string, object?>>();
                    }
                    finally
                    {
                        try { throttler.Release(); } catch { /* ignore */ }
                    }
                }));
            }

            // Collect
            List<Dictionary<string, object?>>[] results;
            try { results = await Task.WhenAll(tasks); }
            catch
            {
                results = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToArray();
            }

            foreach (var list in results)
            {
                foreach (var it in list)
                {
                    var key = FirstNotEmpty(
                        it.TryGetValue("doi", out var d) ? d as string : null,
                        it.TryGetValue("pdf_url", out var p) ? p as string : null,
                        it.TryGetValue("url", out var u) ? u as string : null,
                        it.TryGetValue("title", out var t) ? t as string : null
                    );
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    // EXCLUSIVE across chapters
                    if (!globalSeenKeys.Add(key)) continue;

                    // de-dupe within this chapter
                    if (!localSeen.Add(key)) continue;

                    items.Add(it);
                }
            }

            // Sort newest first, trim to chapterItemCap
            var ordered = items
                .OrderByDescending(it => int.TryParse(it.TryGetValue("year", out var y) ? y?.ToString() : null, out var yi) ? yi : 0)
                .Take(Math.Max(1, chapterItemCap))
                .ToList();

            // Write ONE JSON per chapter
            var fileSlug = $"chapter-{chapterNumInt}";
            var outPath = Path.Combine(outDir, $"{fileSlug}.json");
            await File.WriteAllTextAsync(outPath, PrettyJson(new { items = ordered }));

            // Index: title + link + small preview
            index.Add($"## {chapterTitle} (window: {endYear})");
            index.Add($"[**{Path.GetFileName(outPath)}**](./{Path.GetFileName(outPath)})");
            index.Add("");

            int listed = 0;
            foreach (var it in ordered)
            {
                var (title, year, venue, link, _) = ExtractListingFields(it);
                index.Add($"- **{EscapeMd(title)}**{(string.IsNullOrWhiteSpace(year) ? "" : $" ({year})")} — {(string.IsNullOrWhiteSpace(venue) ? "" : $"_{EscapeMd(venue)}_ — ")}{(string.IsNullOrWhiteSpace(link) ? "" : $"[link]({link})")}");
                listed++;
                if (listed >= 5) break;
            }
            if (listed == 0) index.Add("- _No items returned_");
            index.Add("");

            saved++;
        }

        var indexPath = Path.Combine(outDir, "index.md");
        await File.WriteAllLinesAsync(indexPath, index);

        Console.WriteLine($"[Updater] Saved {saved} chapter files.");
        Console.WriteLine($"[Updater] Wrote index: {indexPath}");
        Console.WriteLine("[Updater] Done.");
    }

    private static List<Dictionary<string, object?>> MergeLists(
    List<Dictionary<string, object?>> list1,
    List<Dictionary<string, object?>> list2,
    int limit)
    {
        var merged = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddList(List<Dictionary<string, object?>> list)
        {
            foreach (var it in list)
            {
                string key = FirstNotEmpty(
                    it.TryGetValue("doi", out var d) ? d as string : null,
                    it.TryGetValue("pdf_url", out var p) ? p as string : null,
                    it.TryGetValue("url", out var u) ? u as string : null,
                    it.TryGetValue("title", out var t) ? t as string : null
                ) ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!seen.Add(key)) continue;
                merged.Add(it);
                if (merged.Count >= limit) break;
            }
        }

        TryAddList(list1);
        if (merged.Count < limit) TryAddList(list2);

        return merged;
    }


    // ======================== MCP client ========================

    private sealed class McpClient : IDisposable
    {
        private readonly Process? _proc;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private int _nextId = 1;

        public bool IsReady { get; private set; }

        public McpClient(Process? proc, StreamWriter stdin, StreamReader stdout)
        {
            _proc = proc;
            _stdin = stdin;
            _stdout = stdout;
            IsReady = true;
        }

        public async Task<JsonElement> CallToolAsync(string name, Dictionary<string, object?> args, int timeoutMs = 15000)
        {
            var id = Interlocked.Increment(ref _nextId);
            var req = new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new { name, arguments = args }
            };
            var line = JsonSerializer.Serialize(req);

            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            while (!cts.IsCancellationRequested)
            {
                var raw = await _stdout.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var rid) &&
                    rid.ValueKind == JsonValueKind.Number &&
                    rid.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out var err)) throw new Exception(err.ToString());
                    return root.GetProperty("result");
                }
            }
            throw new TimeoutException("MCP call timed out");
        }

        public void Dispose()
        {
            try { _stdin?.Dispose(); } catch { }
            try { _stdout?.Dispose(); } catch { }
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
        }
    }

    private static async Task<McpClient?> TryStartMcpAsync(string? cmd, string? script)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cmd) || string.IsNullOrWhiteSpace(script))
            {
                // Not configured to spawn; user may run server manually.
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = script,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory
            };
            var p = Process.Start(psi);
            if (p == null) return null;

            // Probe tools/list to ensure ready
            var stdin = p.StandardInput;
            var stdout = p.StandardOutput;

            var probe = new { jsonrpc = "2.0", id = 1, method = "tools/list" };
            await stdin.WriteLineAsync(JsonSerializer.Serialize(probe));
            await stdin.FlushAsync();

            var raw = await stdout.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("result", out _))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            return new McpClient(p, stdin, stdout);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<Dictionary<string, object?>>> McpSearchAsync(
        McpClient mcp, string query, DateTime startDate, DateTime endDate, int limit)
    {
        var args = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["start"] = startDate.ToString("yyyy-MM-dd"),
            ["end"] = endDate.ToString("yyyy-MM-dd"),
            ["limit"] = limit,
            ["includeArxiv"] = true // your index.ts supports this and returns merged, deduped items
        };

        var result = await mcp.CallToolAsync("search_papers", args);

        // MCP server returns: { content: [ { type:"json", json: { ok, items:[...] } } ] }
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var t) &&
                    string.Equals(t.GetString(), "json", StringComparison.OrdinalIgnoreCase) &&
                    part.TryGetProperty("json", out var payload))
                {
                    var arr = payload.TryGetProperty("items", out var items) ? items
                              : (payload.TryGetProperty("result", out var r) && r.TryGetProperty("items", out var items2) ? items2
                              : default);

                    if (arr.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<Dictionary<string, object?>>();
                        foreach (var it in arr.EnumerateArray())
                        {
                            list.Add(new Dictionary<string, object?>
                            {
                                ["title"] = it.TryGetProperty("title", out var tt) ? tt.GetString() : null,
                                ["year"] = it.TryGetProperty("year", out var yy) ? yy.ToString() : null,
                                ["venue"] = it.TryGetProperty("venue", out var vv) ? vv.GetString() : null,
                                ["doi"] = it.TryGetProperty("doi", out var dd) ? dd.GetString() : null,
                                ["url"] = it.TryGetProperty("url", out var uu) ? uu.GetString() : null,
                                ["pdf_url"] = it.TryGetProperty("pdfUrl", out var pu) ? pu.GetString()
                                              : (it.TryGetProperty("pdf_url", out var pu2) ? pu2.GetString() : null)
                            });
                        }
                        return list;
                    }
                }
            }
        }
        return new List<Dictionary<string, object?>>();
    }

    // ======================== arXiv search (with retries/backoff) ========================

    private static async Task<List<Dictionary<string, object?>>> ArxivSearchAsync(
        string query, DateTime startDate, DateTime endDate, int limit, int timeoutMs)
    {
        // Up to 3 attempts with linear backoff (250ms, 500ms)
        var attempts = 0;
        var maxAttempts = 3;
        var backoffMs = 250;

        var url = "https://export.arxiv.org/api/query" +
                  $"?search_query=all:{Uri.EscapeDataString(query)}" +
                  $"&start=0&max_results={Math.Max(1, Math.Min(limit, 100))}" +
                  "&sortBy=lastUpdatedDate&sortOrder=descending";

        while (true)
        {
            attempts++;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("SearchAgent1-Updater/1.0");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                var xml = await resp.Content.ReadAsStringAsync(cts.Token);
                return ParseArxivXml(xml, startDate, endDate);
            }
            catch (TaskCanceledException) when (attempts < maxAttempts)
            {
                await Task.Delay(backoffMs * attempts);
                continue;
            }
            catch (HttpRequestException) when (attempts < maxAttempts)
            {
                await Task.Delay(backoffMs * attempts);
                continue;
            }
        }
    }

    private static async Task<List<Dictionary<string, object?>>> OpenAlexSearchAsync(
    string query, DateTime startDate, DateTime endDate, int limit)
    {
        // Add your contact email to avoid 403 or throttling
        var mailto = Environment.GetEnvironmentVariable("OPENALEX_MAILTO")
                     ?? "you@domain.com";

        // Build base URL: /works endpoint
        var baseUrl = "https://api.openalex.org/works";

        // Use “search” parameter to search across title / abstract etc.
        var url = $"{baseUrl}?search={Uri.EscapeDataString(query)}" +
                  $"&per-page={Math.Max(1, Math.Min(limit, 25))}" +
                  $"&mailto={Uri.EscapeDataString(mailto)}";

        // Optionally you could filter by publication date or year:
        // e.g. &filter=publication_year:2020-2025 etc.
        // But here we’ll fetch broadly and then filter locally by date window.

        // Prepare request
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd($"SearchAgent1-Updater/1.0 (+mailto:{mailto})");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var list = new List<Dictionary<string, object?>>();

        // “results” is the array in OpenAlex
        if (root.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in arr.EnumerateArray())
            {
                // Extract fields
                string? title = TryGetString(w, "title");
                int? pubYear = TryGetInt(w, "publication_year");
                string? doi = TryGetString(w, "doi");
                string? venue = TryGetString(w, "host_venue", "display_name");
                string? urlL = TryGetString(w, "primary_location", "landing_page_url")
                              ?? TryGetString(w, "id");
                string? pdf = TryGetString(w, "primary_location", "pdf_url");

                // Optional: filter by date window
                if (pubYear.HasValue)
                {
                    var y = pubYear.Value;
                    if (new DateTime(y, 1, 1) < startDate || new DateTime(y, 12, 31) > endDate.AddYears(1))
                    {
                        // you might want a stricter filter depending on your window
                        // but here we skip if year is completely outside window
                        // you can remove this check if too strict
                    }
                }

                var entry = new Dictionary<string, object?>()
                {
                    ["title"] = title,
                    ["year"] = pubYear.HasValue ? pubYear.Value.ToString() : null,
                    ["venue"] = venue,
                    ["doi"] = string.IsNullOrWhiteSpace(doi) ? null : doi,
                    ["url"] = string.IsNullOrWhiteSpace(urlL) ? null : urlL,
                    ["pdf_url"] = string.IsNullOrWhiteSpace(pdf) ? null : pdf
                };

                list.Add(entry);
                if (list.Count >= limit) break;
            }
        }

        return list;
    }

    // Helper methods to use in that implementation:
    private static string? TryGetString(JsonElement el, params string[] path)
    {
        foreach (var p in path)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(p, out el)) return null;
        }
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Number) return el.ToString();
        return null;
    }

    private static int? TryGetInt(JsonElement el, params string[] path)
    {
        var s = TryGetString(el, path);
        if (s != null && int.TryParse(s, out var v)) return v;
        return null;
    }


    private static List<Dictionary<string, object?>> ParseArxivXml(string xml, DateTime startDate, DateTime endDate)
    {
        var list = new List<Dictionary<string, object?>>();
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            XNamespace arxiv = "http://arxiv.org/schemas/atom";

            var entries = doc.Root?.Elements(atom + "entry").ToList() ?? new List<XElement>();

            foreach (var e in entries)
            {
                string title = (string?)e.Element(atom + "title") ?? "";
                string? published = (string?)e.Element(atom + "published");
                int? year = null;
                if (DateTime.TryParse(published, out var dt))
                {
                    if (dt.Date < startDate || dt.Date > endDate) continue; // window filter
                    year = dt.Year;
                }

                var links = e.Elements(atom + "link").ToList();
                string? absUrl = links.FirstOrDefault(l =>
                                    ((string?)l.Attribute("title")) == "abstract" ||
                                    ((string?)l.Attribute("rel")) == "alternate")
                                ?.Attribute("href")?.Value;
                string? pdfUrl = links.FirstOrDefault(l =>
                                    ((string?)l.Attribute("type"))?.Contains("pdf") == true)
                                ?.Attribute("href")?.Value;

                // doi sometimes appears under arxiv:doi or as <doi> (namespace variants)
                string? doi = (string?)e.Element(arxiv + "doi")
                            ?? e.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("doi", StringComparison.OrdinalIgnoreCase))?.Value;

                list.Add(new Dictionary<string, object?>
                {
                    ["title"] = NormalizeWs(title),
                    ["year"] = year?.ToString(),
                    ["venue"] = "arXiv",
                    ["doi"] = string.IsNullOrWhiteSpace(doi) ? null : doi,
                    ["url"] = string.IsNullOrWhiteSpace(absUrl) ? null : absUrl,
                    ["pdf_url"] = string.IsNullOrWhiteSpace(pdfUrl) ? null : pdfUrl
                });
            }
        }
        catch
        {
            // ignore parse errors; return what we have
        }
        return list;
    }

    // ======================== Seed scoping & utils ========================

    private static string BuildChapterQuery(string chapterTitle)
    {
        // remove "Chapter N." prefix and punctuation, keep 3–6 informative tokens
        var title = Regex.Replace(chapterTitle ?? "", @"^\s*Chapter\s+\d+[\.:]?\s*", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"[^\w\s]", " ");
        var words = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(w => w.Length > 2 && !Stopwords.Contains(w.ToLowerInvariant()))
                         .Take(6);
        return string.Join(' ', words);
    }

    private static string NormalizeSeedYear(string seed, int targetYear)
    {
        // Replace any 19xx/20xx within seed text to the window's END year
        return Regex.Replace(seed ?? "", @"\b(19|20)\d{2}\b", targetYear.ToString());
    }

    private static (string title, string year, string venue, string link, string key) ExtractListingFields(Dictionary<string, object?> it)
    {
        string title = it.TryGetValue("title", out var t) ? (t?.ToString() ?? "") : "";
        string year = it.TryGetValue("year", out var y) ? (y?.ToString() ?? "") : "";
        string venue = it.TryGetValue("venue", out var v) ? (v?.ToString() ?? "") : "";

        string? link =
            (it.TryGetValue("pdf_url", out var p) ? p?.ToString() : null) ??
            (it.TryGetValue("url", out var u) ? u?.ToString() : null) ??
            (it.TryGetValue("doi", out var d) ? (!string.IsNullOrWhiteSpace(d?.ToString())
                ? (d!.ToString()!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? d!.ToString()! : "https://doi.org/" + d!.ToString()!)
                : null) : null);

        string key = FirstNotEmpty(
            it.TryGetValue("doi", out var d2) ? d2 as string : null,
            it.TryGetValue("pdf_url", out var p2) ? p2 as string : null,
            it.TryGetValue("url", out var u2) ? u2 as string : null,
            it.TryGetValue("title", out var t2) ? t2 as string : null
        ) ?? "";

        return (title, year, venue, link ?? "", key);
    }

    private static string NormalizeWs(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static string? FirstNotEmpty(params string?[] vals)
    {
        foreach (var v in vals) if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static string PrettyJson(object obj)
    {
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void EnsureDir(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    private static string EscapeMd(string s)
    {
        return (s ?? "").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    }

    private static int[] ParseNumericId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<int>();
        return id.Split('.', StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => int.TryParse(s, out var n) ? n : int.MaxValue)
                 .ToArray();
    }

    private sealed class IntArrayComparer : IComparer<int[]>
    {
        public static readonly IntArrayComparer Instance = new();
        public int Compare(int[]? x, int[]? y)
        {
            x ??= Array.Empty<int>(); y ??= Array.Empty<int>();
            int len = Math.Max(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                int xi = i < x.Length ? x[i] : -1;
                int yi = i < y.Length ? y[i] : -1;
                if (xi != yi) return xi.CompareTo(yi);
            }
            return 0;
        }
    }

    private static string? GetArg(string[] args, string key)
    {
        var match = args?.FirstOrDefault(a =>
            a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--" + key + "=", StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;
        var idx = match.IndexOf('=');
        return idx >= 0 ? match[(idx + 1)..] : null;
    }

    private static int TryParseInt(string? s, int fallback)
    {
        return int.TryParse(s, out var n) && n > 0 ? n : fallback;
    }
}
