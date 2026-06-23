import { z } from "zod";
import { randomUUID } from "node:crypto";
import { appendFileSync, existsSync, mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
// Trust AGEN_LINK_PROJECT_ROOT only when this process is actually running inside that project.
// The Antigravity CLI reads a GLOBAL config (~/.gemini/config/mcp_config.json) holding the env of the
// LAST Unity-started project — if the user runs agy standalone in some other folder, that stale root
// must not receive this session's memory. cwd is the CLI's launch dir (the real project), so fall back
// to it whenever cwd is outside the env root.
const projectRoot = () => {
    const env = process.env.AGEN_LINK_PROJECT_ROOT;
    const cwd = process.cwd();
    if (!env)
        return cwd;
    const norm = (p) => p.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
    return (norm(cwd) + "/").startsWith(norm(env) + "/") ? env : cwd;
};
const author = () => process.env.AGEN_LINK_CLI ?? "unknown";
const memoryDir = () => join(projectRoot(), "AgenLink~", "memory");
const storePath = () => join(memoryDir(), "memory.jsonl");
function readEntries() {
    const p = storePath();
    if (!existsSync(p))
        return [];
    const out = [];
    for (const line of readFileSync(p, "utf8").split("\n")) {
        const s = line.trim();
        if (!s)
            continue;
        try {
            out.push(JSON.parse(s));
        }
        catch {
            /* skip malformed line */
        }
    }
    return out;
}
export const memoryTools = [
    {
        name: "agen_memory_append",
        description: "Append a durable note to the SHARED project memory (read by both the Claude and Antigravity CLIs working " +
            "in this Unity project). Use it for architecture decisions, gotchas, where a system lives, conventions — " +
            "anything the other CLI should not have to rediscover. Keep each note focused.",
        schema: {
            text: z.string().min(1).describe("The note to remember (a focused fact or decision)."),
            tags: z.array(z.string()).optional().describe("Optional keywords to aid later search."),
            scope: z.string().optional().describe("Optional area/system this note is about (e.g. 'combat', 'ui')."),
        },
        run: async (_port, a) => {
            mkdirSync(memoryDir(), { recursive: true });
            const entry = {
                id: randomUUID(),
                ts: new Date().toISOString(),
                author: author(),
                scope: a.scope ? String(a.scope) : undefined,
                tags: Array.isArray(a.tags) ? a.tags.map(String) : undefined,
                text: String(a.text ?? "").slice(0, 8192),
            };
            appendFileSync(storePath(), JSON.stringify(entry) + "\n", "utf8");
            return { ok: true, id: entry.id };
        },
    },
    {
        name: "agen_memory_search",
        description: "Search the SHARED project memory BEFORE scanning the project. Returns notes (from either CLI) whose " +
            "text/tags/scope match the query terms, most relevant first. Call this early to reuse prior understanding.",
        schema: {
            query: z.string().min(1).describe("Keywords to search for."),
            limit: z.number().int().positive().max(100).optional().describe("Max results (default 20)."),
            scope: z.string().optional().describe("Restrict to this scope."),
        },
        run: async (_port, a) => {
            const limit = typeof a.limit === "number" ? a.limit : 20;
            const scope = a.scope ? String(a.scope) : undefined;
            const terms = String(a.query ?? "")
                .toLowerCase()
                .split(/\s+/)
                .filter(Boolean);
            const results = readEntries()
                .filter((e) => !scope || e.scope === scope)
                .map((e) => {
                const hay = `${e.text} ${e.tags?.join(" ") ?? ""} ${e.scope ?? ""}`.toLowerCase();
                return { e, score: terms.reduce((n, t) => n + (hay.includes(t) ? 1 : 0), 0) };
            })
                .filter((x) => x.score > 0)
                .sort((x, y) => y.score - x.score || y.e.ts.localeCompare(x.e.ts))
                .slice(0, limit)
                .map((x) => x.e);
            return { count: results.length, results };
        },
    },
    {
        name: "agen_memory_list",
        description: "List the most recent SHARED project memory notes (newest first), optionally filtered by scope.",
        schema: {
            limit: z.number().int().positive().max(200).optional().describe("Max results (default 30)."),
            scope: z.string().optional().describe("Restrict to this scope."),
        },
        run: async (_port, a) => {
            const limit = typeof a.limit === "number" ? a.limit : 30;
            const scope = a.scope ? String(a.scope) : undefined;
            const results = readEntries()
                .filter((e) => !scope || e.scope === scope)
                .slice(-limit)
                .reverse();
            return { count: results.length, results };
        },
    },
];
