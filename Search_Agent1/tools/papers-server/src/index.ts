// Minimal MCP-compatible JSON-RPC server over stdio
// Fast version: timeouts per source + partial results

import fetch from "node-fetch";
import { XMLParser } from "fast-xml-parser";
import readline from "node:readline";

// ----- Types -----
type JsonRpcId = string | number | null;
interface JsonRpcRequest { jsonrpc: "2.0"; id?: JsonRpcId; method: string; params?: any; }
interface JsonRpcResponse { jsonrpc: "2.0"; id: JsonRpcId; result?: any; error?: { code: number; message: string; data?: any }; }

type Paper = {
    title: string;
    authors: string[];
    abstract?: string;
    url?: string;
    pdfUrl?: string;
    doi?: string;
    venue?: string;
    year?: number;
    source: "OpenAlex" | "arXiv";
};

// ----- Utils -----
const norm = (s: string) => s.toLowerCase().replace(/\s+/g, " ").trim();

async function fetchWithTimeout(url: string, ms: number, init: any = {}) {
    const ac = new AbortController();
    const id = setTimeout(() => ac.abort(), ms);
    try { return await fetch(url, { ...init, signal: ac.signal }); }
    finally { clearTimeout(id); }
}

const send = (res: JsonRpcResponse) => process.stdout.write(JSON.stringify(res) + "\n");
const ok = (id: JsonRpcId, result: any): JsonRpcResponse => ({ jsonrpc: "2.0", id, result });
const err = (id: JsonRpcId, code: number, message: string, data?: any): JsonRpcResponse => ({ jsonrpc: "2.0", id, error: { code, message, data } });

// ----- Sources with timeouts -----
async function openAlexSearch(q: string, start: string, end: string, limit: number, timeoutMs = 6000): Promise<Paper[]> {
    const base = process.env.OPENALEX_BASE ?? "https://api.openalex.org";
    const url = new URL("/works", base);
    url.searchParams.set("search", q);
    url.searchParams.set("from_publication_date", start);
    url.searchParams.set("to_publication_date", end);
    url.searchParams.set("per_page", String(Math.min(limit, 25)));

    const resp = await fetchWithTimeout(url.toString(), timeoutMs, { headers: { "User-Agent": "papers-mcp/1.0" } });
    if (!resp.ok) throw new Error(`OpenAlex ${resp.status}`);
    const data: any = await resp.json();

    const items: Paper[] = (data.results ?? []).map((w: any) => ({
        title: w.title,
        authors: (w.authorships ?? []).map((a: any) => a.author?.display_name).filter(Boolean),
        abstract: w.abstract,
        url: w.primary_location?.source?.homepage_url || w.primary_location?.landing_page_url || w.id,
        pdfUrl: w.primary_location?.pdf_url ?? undefined,
        doi: (w.doi || "")?.replace(/^https?:\/\/doi.org\//i, "") || undefined,
        venue: w.host_venue?.display_name,
        year: w.from_publication_date ? Number(String(w.from_publication_date).slice(0, 4)) : undefined,
        source: "OpenAlex"
    }));
    return items;
}

async function arxivSearch(q: string, start: string, end: string, limit: number, timeoutMs = 4000): Promise<Paper[]> {
    const base = process.env.ARXIV_BASE ?? "https://export.arxiv.org/api";
    const url = `${base}/query?search_query=all:${encodeURIComponent(q)}&start=0&max_results=${Math.min(limit, 25)}&sortBy=lastUpdatedDate&sortOrder=descending`;
    const resp = await fetchWithTimeout(url, timeoutMs);
    if (!resp.ok) throw new Error(`arXiv ${resp.status}`);
    const text = await resp.text();

    const parser = new XMLParser({ ignoreAttributes: false });
    const feed: any = parser.parse(text);
    const entries = Array.isArray(feed.feed?.entry) ? feed.feed.entry : (feed.feed?.entry ? [feed.feed.entry] : []);
    const startMs = Date.parse(start);
    const endMs = Date.parse(end);

    const items: Paper[] = entries.map((e: any) => {
        const links = Array.isArray(e.link) ? e.link : (e.link ? [e.link] : []);
        const absUrl = links.find((l: any) => l["@_title"] === "abstract" || l["@_rel"] === "alternate")?.["@_href"];
        const pdfUrl = links.find((l: any) => (l["@_type"] || "").includes("pdf"))?.["@_href"];
        const year = e.published ? Number(String(e.published).slice(0, 4)) : undefined;
        return {
            title: (e.title ?? "").toString(),
            authors: (Array.isArray(e.author) ? e.author : [e.author]).map((a: any) => a?.name).filter(Boolean),
            abstract: (e.summary ?? "").toString(),
            url: absUrl ?? e.id,
            pdfUrl,
            doi: e.doi ?? undefined,
            venue: "arXiv",
            year,
            source: "arXiv"
        } as Paper;
    }).filter((p: Paper) => {
        if (!p.year) return true;
        const approxMs = Date.parse(`${p.year}-06-30`);
        return approxMs >= startMs && approxMs <= endMs;
    });

    return items;
}

// ----- Tools -----
const TOOL_DESCRIPTORS = [
    {
        name: "search_papers",
        description: "Search scholarly papers (fast; returns partial results if some sources timeout).",
        input_schema: {
            type: "object",
            required: ["query", "start", "end"],
            properties: {
                query: { type: "string" },
                start: { type: "string", description: "YYYY-MM-DD" },
                end: { type: "string", description: "YYYY-MM-DD" },
                limit: { type: "number", default: 10 },
                includeArxiv: { type: "boolean", default: false } // default false for speed/reliability
            }
        }
    },
    {
        name: "get_pdf_url",
        description: "Given a DOI or arXiv id (or URL), returns a best-guess PDF URL.",
        input_schema: {
            type: "object",
            required: ["id"],
            properties: { id: { type: "string" } }
        }
    }
] as const;

const TOOLS: Record<string, (args: any) => Promise<any>> = {
    async search_papers(args: { query: string; start: string; end: string; limit?: number; includeArxiv?: boolean }) {
        const { query, start, end, limit = 10, includeArxiv = false } = args;
        if (!query || !start || !end) throw new Error("Missing required fields: query, start, end");

        const tasks: Promise<Paper[]>[] = [
            openAlexSearch(query, start, end, limit).catch(() => [])
        ];
        if (includeArxiv) {
            tasks.push(arxivSearch(query, start, end, Math.min(limit, 10)).catch(() => []));
        }

        // Run in parallel; each source has its own timeout; we merge whatever returns
        const arrays = await Promise.all(tasks);
        const merged = ([] as Paper[]).concat(...arrays);

        // Dedup
        const seen = new Set<string>();
        const unique: Paper[] = [];
        for (const p of merged) {
            const key = p.doi ? `doi:${p.doi.toLowerCase()}` : `t:${norm(p.title)}`;
            if (seen.has(key)) continue;
            seen.add(key);
            unique.push(p);
        }

        unique.sort((x, y) => ((y.year ?? 0) - (x.year ?? 0)));
        return { ok: true, items: unique.slice(0, limit) };
    },

    async get_pdf_url(args: { id: string }) {
        const { id } = args;
        if (!id) throw new Error("Missing id");
        if (/^10\./i.test(id)) return { pdfUrl: `https://doi.org/${id}` };
        if (/arxiv\.org\/abs\//i.test(id)) {
            const short = id.split("/abs/")[1];
            return { pdfUrl: `https://arxiv.org/pdf/${short}.pdf` };
        }
        if (/^\d{4}\.\d{4,5}(v\d+)?$/i.test(id)) return { pdfUrl: `https://arxiv.org/pdf/${id}.pdf` };
        return { pdfUrl: id };
    }
};

// ----- JSON-RPC stdio loop -----
const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: false });

function isJsonRpcRequest(obj: any): obj is JsonRpcRequest {
    return obj && typeof obj === "object" && obj.jsonrpc === "2.0" && typeof obj.method === "string";
}

rl.on("line", async (line: string) => {
    let raw: any;
    try { raw = JSON.parse(line); } catch { return; }
    if (!isJsonRpcRequest(raw)) return;

    const id = raw.id ?? null;
    try {
        if (raw.method === "tools/list") {
            send(ok(id, { tools: TOOL_DESCRIPTORS }));
            return;
        }
        if (raw.method === "tools/call") {
            const name = raw.params?.name as string;
            const args = raw.params?.arguments ?? {};
            const fn = TOOLS[name];
            if (!fn) { send(err(id, -32601, `Unknown tool: ${name}`)); return; }
            const result = await fn(args);
            send(ok(id, { content: [{ type: "json", json: result }] }));
            return;
        }
        send(err(id, -32601, `Method not found: ${raw.method}`));
    } catch (e: any) {
        send(err(id, -32000, e?.message ?? "Server error"));
    }
});

// Optional visual cue on stderr
process.stderr.write("papers MCP server ready\n");
