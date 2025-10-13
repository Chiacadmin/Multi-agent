// ChapterScanner.cs — ignore TOC, scan full body, support “Chapter N.” / “N.M …” / # / ##
// Produces sections with: Id, Title, ChapterId, ChapterTitle, Body

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public sealed class ChapterSection
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ChapterId { get; set; } = "";
    public string ChapterTitle { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class ChapterScanner
{
    private static readonly Regex ChapterLine = new(
    @"^\s*Chapter\s+(?<num>\d+)[\.:]\s+(?<title>.+?)\s*$",
    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SectionLine = new(
        @"^\s*(?<num>\d+(?:\.\d+)+)\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MdHeading = new(
        @"^(#{1,2})\s+(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public List<ChapterSection> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        // Normalize and drop obvious TOC: start scanning from the first “Chapter N.” or first H1
        text = text.Replace("\r\n", "\n");

        int bodyStart = FindBodyStart(text);
        if (bodyStart < 0) return new();
        var body = text.Substring(bodyStart);

        // Collect heading nodes
        var nodes = new List<(int idx, int len, int level, string id, string title)>();

        foreach (Match m in ChapterLine.Matches(body))
        {
            var num = m.Groups["num"].Value;
            var title = $"Chapter {num}. {TrimEndPage(m.Groups["title"].Value)}";
            nodes.Add((m.Index, m.Length, 1, num, title));
        }
        foreach (Match m in SectionLine.Matches(body))
        {
            var num = m.Groups["num"].Value;
            var title = $"{num} {TrimEndPage(m.Groups["title"].Value)}";
            nodes.Add((m.Index, m.Length, 2, num, title));
        }
        foreach (Match m in MdHeading.Matches(body))
        {
            var lvl = m.Groups[1].Value.Length;
            if (lvl > 2) continue;
            var title = m.Groups["title"].Value.Trim();
            var id = ExtractNumericId(title) ?? Slug(title);
            nodes.Add((m.Index, m.Length, lvl, id, title));
        }

        nodes.Sort((a, b) => a.idx.CompareTo(b.idx));
        if (nodes.Count == 0) return new();

        // Slice
        var sections = new List<ChapterSection>();
        string? currentChId = null, currentChTitle = null;

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            int start = n.idx + n.len;
            int end = (i + 1 < nodes.Count) ? nodes[i + 1].idx : body.Length;
            if (end < start) end = start;
            var seg = body.Substring(start, end - start).Trim();

            if (n.level == 1)
            {
                currentChId = ExtractNumericId(n.id) ?? n.id;
                currentChTitle = n.title;
                sections.Add(new ChapterSection
                {
                    Id = currentChId!,
                    Title = currentChTitle!,
                    ChapterId = currentChId!,
                    ChapterTitle = currentChTitle!,
                    Body = seg
                });
            }
            else
            {
                var chId = currentChId ?? n.id.Split('.')[0];
                var chTitle = currentChTitle ?? $"Chapter {chId}";
                sections.Add(new ChapterSection
                {
                    Id = n.id,
                    Title = n.title,
                    ChapterId = chId,
                    ChapterTitle = chTitle,
                    Body = seg
                });
            }
        }

        // De-dup sections by (ChapterId|Title)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sections = sections.Where(s => seen.Add($"{s.ChapterId}|{s.Title}")).ToList();

        return sections;
    }

    private static int FindBodyStart(string s)
    {
        var m1 = ChapterLine.Match(s);
        if (m1.Success) return m1.Index;
        foreach (Match m in MdHeading.Matches(s))
            if (m.Groups[1].Value.Length == 1) return m.Index; // first H1
        return -1;
    }

    private static string? ExtractNumericId(string titleOrId)
    {
        var m = Regex.Match(titleOrId, @"^(?<num>\d+(?:\.\d+)*)\b");
        return m.Success ? m.Groups["num"].Value : null;
    }

    private static string TrimEndPage(string s)
    {
        return Regex.Replace(s, @"\s+\d+\s*$", "").Trim();
    }

    private static string Slug(string s)
    {
        s = s.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9]+", "-");
        s = Regex.Replace(s, @"-+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "section" : s;
    }
}
