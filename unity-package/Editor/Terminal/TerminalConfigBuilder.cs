using System.Collections.Generic;

namespace AgenLink.Terminal
{
    /// <summary>Builds the claude argv for the full-power embedded terminal (the user's real config +
    /// the Unity MCP server added on top). No restrictive --settings here — this is a full interactive session.</summary>
    internal static class TerminalConfigBuilder
    {
        public static List<string> BuildClaudeArgs()
        {
            var args = new List<string>();
            // ConfigBuilder.WriteMcpConfigTemp() writes %TEMP%\agenlink-mcp.json describing the Unity
            // MCP server. Passing it via --mcp-config ADDS it to the user's configured servers (additive
            // by default; not --strict-mcp-config), so their own servers/skills/login all still apply.
            // If the MCP server isn't built/found, launch claude anyway (just without live Unity awareness)
            // rather than failing the whole session — WriteMcpConfigTemp throws when it can't resolve.
            try
            {
                if (ConfigBuilder.ResolveMcpServerPath() != null)
                {
                    string mcp = ConfigBuilder.WriteMcpConfigTemp();
                    if (!string.IsNullOrEmpty(mcp))
                    {
                        args.Add("--mcp-config");
                        args.Add(mcp);
                    }
                }
            }
            catch { /* MCP server unavailable — proceed without it */ }
            return args;
        }

        /// <summary>Antigravity's Unity bridge is configured via ~/.gemini/config/mcp_config.json
        /// (written by ConfigBuilder.WriteAntigravityMcpConfig), not via argv — so no extra args. A bare
        /// `agy` launches the interactive TUI.</summary>
        public static List<string> BuildAntigravityArgs() => new List<string>();
    }
}
