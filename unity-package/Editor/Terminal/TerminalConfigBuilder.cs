using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Terminal
{
    /// <summary>Builds the claude argv for the full-power embedded terminal (the user's real config +
    /// the Unity MCP server added on top). No restrictive --settings here — this is a full interactive session.</summary>
    internal static class TerminalConfigBuilder
    {
        // Strong, short steering that outranks the model's default "ask the user / write a script" habits.
        // Added via --append-system-prompt (not --system-prompt) so it augments, never replaces, the CLI's own.
        private const string SystemPrompt =
            "You are connected to a LIVE Unity Editor via agen_* MCP tools that read AND modify it. Rules: query " +
            "editor state with tools (agen_get_scene_hierarchy, agen_find_gameobjects, agen_get_gameobject, " +
            "agen_read_console) instead of asking the user; perform editor operations with tools " +
            "(agen_create_gameobject, agen_set_component_properties, agen_manage_scene, agen_playmode) instead of " +
            "writing editor scripts; call agen_get_gameobject before setting properties; after editing .cs run " +
            "agen_refresh_assets then poll agen_get_compile_errors; after play-mode changes the bridge reconnects " +
            "within seconds — retry, don't abandon tools; scene edits are unsaved — ask before saving.";

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
                    // Only steer the model toward the tools when the tools are actually wired up.
                    if (SupportsAppendSystemPrompt())
                    {
                        args.Add("--append-system-prompt");
                        args.Add(SystemPrompt);
                    }
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

        /// <summary>Whether the installed claude CLI accepts --append-system-prompt. Old CLIs abort on an
        /// unknown flag, so we sniff `claude --help` once and cache the answer for the session.</summary>
        private static bool SupportsAppendSystemPrompt()
        {
            const string key = "AgenLink.Claude.AppendSysPrompt";
            string cached = SessionState.GetString(key, "");
            if (cached == "1") return true;
            if (cached == "0") return false;

            bool supported = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ClaudeCli.ResolveExe(),
                    Arguments = "--help",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    string help = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { /* ignore */ } }
                    supported = help.Contains("--append-system-prompt");
                }
            }
            catch { supported = false; }

            SessionState.SetString(key, supported ? "1" : "0");
            return supported;
        }

        internal static void ReportMcpFailure(string reason)
        {
            LaunchDiagnostics.McpFailure = reason;
            UnityEngine.Debug.LogError(
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
