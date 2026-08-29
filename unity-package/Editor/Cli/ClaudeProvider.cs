using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using AgenLink.History;
using AgenLink.Terminal;

namespace AgenLink.Cli
{
    /// <summary>
    /// Anthropic's Claude Code CLI. The npm global install ships a native exe at
    /// %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe (claude.cmd just calls it);
    /// launching the exe directly avoids shell-quoting issues.
    /// </summary>
    internal sealed class ClaudeProvider : CliProvider
    {
        public override string Id => "claude";
        public override string DisplayName => "Claude";
        public override string SettingsHeading => "Claude CLI";
        public override string ExeFileName => "claude.exe";
        public override string AutoDetectHint =>
            "Auto-detected from npm global / PATH. Set only if Claude isn't found.";
        public override string ResumeHint =>
            "Reopen it with “claude --continue” in the Terminal.";
        public override Color AccentColor => new Color(0xE0 / 255f, 0x8A / 255f, 0x66 / 255f); // coral

        public override string PathOverride
        {
            get => BridgeSettings.ClaudePath;
            set => BridgeSettings.ClaudePath = value;
        }

        public override string ResolveExe()
        {
            string custom = PathOverride;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string primary = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (File.Exists(primary)) return primary;

            string onPath = ScanPath(ExeFileName);
            if (onPath != null) return onPath;

            throw new Exception(
                "Could not find claude.exe. Install Claude Code (npm i -g @anthropic-ai/claude-code) " +
                "or make sure it is on PATH.");
        }

        /// <summary>
        /// ConfigBuilder.WriteMcpConfigTemp() writes a %TEMP% file describing the Unity MCP server.
        /// Passing it via --mcp-config ADDS it to the user's configured servers (additive by default;
        /// not --strict-mcp-config), so their own servers/skills/login all still apply. If the MCP
        /// server isn't built we launch claude anyway rather than failing the whole session — but
        /// LOUDLY, because without it the CLI has zero agen_* tools and silently falls back to asking
        /// the user / writing editor scripts.
        /// </summary>
        public override List<string> BuildArgs()
        {
            var args = new List<string>();
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
                    if (HelpMentions(ResolveExe(), "--append-system-prompt", "AgenLink.Claude.AppendSysPrompt"))
                    {
                        args.Add("--append-system-prompt");
                        args.Add(SystemPrompt);
                    }
                }
                else
                {
                    TerminalConfigBuilder.ReportMcpFailure("mcp-server/build/index.js was not found.");
                }
            }
            catch (Exception e)
            {
                TerminalConfigBuilder.ReportMcpFailure(e.Message);
            }
            return args;
        }

        public override List<Conversation> LoadHistory(string projectRoot) => TranscriptReader.LoadAll(projectRoot);
    }
}
