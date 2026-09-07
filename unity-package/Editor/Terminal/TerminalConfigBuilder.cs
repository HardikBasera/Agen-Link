using System;
using UnityEngine;

namespace AgenLink.Terminal
{
    /// <summary>Reports a failure to wire up the Unity MCP tools. Argv construction lives on the
    /// CliProvider for each CLI (see Editor/Cli/).</summary>
    internal static class TerminalConfigBuilder
    {
        internal static void ReportMcpFailure(string reason)
        {
            LaunchDiagnostics.McpFailure = reason;
            UnityEngine.Debug.LogError(
                "[Agen-Link] Unity tools NOT loaded — the CLI is starting without the agen_* MCP tools (" +
                reason + "). It cannot see or edit the Editor and will fall back to asking you / writing " +
                "scripts. Run install\\setup.cmd (or `npm run build` in mcp-server), or set the path in " +
                "Agen-Link ▸ Settings, then Restart the session.");
        }
    }
}
