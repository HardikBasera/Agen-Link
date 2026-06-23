#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { tools } from "./tools.js";
const PORT = Number.parseInt(process.env.AGEN_LINK_PORT ?? "6577", 10);
const server = new McpServer({
    name: "agen-link",
    version: "0.1.0",
});
for (const tool of tools) {
    server.tool(tool.name, tool.description, tool.schema, async (args) => {
        try {
            const data = await tool.run(PORT, args ?? {});
            return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
        }
        catch (err) {
            return {
                content: [{ type: "text", text: `Error: ${err.message}` }],
                isError: true,
            };
        }
    });
}
async function main() {
    const transport = new StdioServerTransport();
    await server.connect(transport);
    // stdout is the MCP JSON-RPC channel — log only to stderr.
    process.stderr.write(`[agen-link-mcp] ready; targeting Agen-Link on 127.0.0.1:${PORT}\n`);
}
main().catch((e) => {
    process.stderr.write(`[agen-link-mcp] fatal: ${e?.message ?? String(e)}\n`);
    process.exit(1);
});
