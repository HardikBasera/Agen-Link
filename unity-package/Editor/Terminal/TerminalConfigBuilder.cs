using System;
using System.Collections.Generic;
using UnityEngine;

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
            // If the MCP server isn't built/found we launch claude anyway (rather than failing the whole
            // session) — but LOUDLY: without this config the CLI has zero agen_* tools and silently falls
            // back to asking the user / writing editor scripts, so we surface the failure to the Terminal tab.
            try
            {
                string mcp = ConfigBuilder.ResolveMcpServerPath() != null
                    ? ConfigBuilder.WriteMcpConfigTemp()
                    : null;
                if (!string.IsNullOrEmpty(mcp))
                {
                    args.Add("--mcp-config");
                    args.Add(mcp);
                    LaunchDiagnostics.McpFailure = null;
                }
                else
                {
                    ReportMcpFailure("mcp-server/build/index.js was not found.");
                }
            }
            catch (Exception e)
            {
                ReportMcpFailure(e.Message);
            }
            return args;
        }

        internal static void ReportMcpFailure(string reason)
        {
            LaunchDiagnostics.McpFailure = reason;
            Debug.LogError(
                "[Agen-Link] Unity tools NOT loaded — the CLI is starting without the agen_* MCP tools (" +
                reason + "). It cannot see or edit the Editor and will fall back to asking you / writing " +
                "scripts. Run install\\setup.cmd (or `npm run build` in mcp-server), or set the path in " +
                "Agen-Link ▸ Settings, then Restart the session.");
        }

        /// <summary>Antigravity's Unity bridge is configured via ~/.gemini/config/mcp_config.json
        /// (written by ConfigBuilder.WriteAntigravityMcpConfig), not via argv — so no extra args. A bare
        /// `agy` launches the interactive TUI.</summary>
        public static List<string> BuildAntigravityArgs() => new List<string>();
    }
}
