using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgenLink.History;
using AgenLink.Terminal;

namespace AgenLink.Cli
{
    /// <summary>
    /// Google's Antigravity CLI (<c>agy</c>) — a native Go TUI agent, the successor to the Gemini CLI.
    /// A normal console executable, so (like claude.exe) we launch it directly, no wrapper.
    /// </summary>
    internal sealed class AntigravityProvider : CliProvider
    {
        public override string Id => "antigravity";
        public override string DisplayName => "Antigravity";
        public override string SettingsHeading => "Antigravity CLI (agy)";
        public override string ExeFileName => "agy.exe";
        public override string AutoDetectHint =>
            "Auto-detected from %LOCALAPPDATA%\\agy. Set only if agy isn't found.";
        public override string ResumeHint =>
            "Antigravity keeps its replies in its own store — reopen this conversation with " +
            "“agy --continue” in the Terminal.";
        public override Color AccentColor => new Color(0x8B / 255f, 0x9C / 255f, 0xF6 / 255f); // violet

        public override string PathOverride
        {
            get => BridgeSettings.AntigravityPath;
            set => BridgeSettings.AntigravityPath = value;
        }

        public override string ResolveExe()
        {
            string custom = PathOverride;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;

            // Standard install location: %LOCALAPPDATA%\agy\bin\agy.exe
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string primary = Path.Combine(local, "agy", "bin", "agy.exe");
            if (File.Exists(primary)) return primary;

            string onPath = ScanPath(ExeFileName);
            if (onPath != null) return onPath;

            throw new Exception(
                "Could not find the Antigravity CLI (agy.exe). Install it from " +
                "https://antigravity.google/docs/cli-install, or set its path in Agen-Link ▸ Settings ▸ Antigravity CLI.");
        }

        /// <summary>
        /// agy's Unity bridge comes from the HOME-level ~/.gemini/config/mcp_config.json we write here
        /// (it has no --mcp-config flag), so a bare `agy` launches the interactive TUI with no extra
        /// argv. agy auto-loads AGENTS.md natively, so no context wiring is needed either. The bridge
        /// is optional — agy still runs without it — but a failed config means zero agen_* tools, so
        /// surface it loudly rather than swallowing it.
        /// </summary>
        public override List<string> BuildArgs()
        {
            try
            {
                if (ConfigBuilder.WriteAntigravityMcpConfig()) LaunchDiagnostics.McpFailure = null;
                else TerminalConfigBuilder.ReportMcpFailure("mcp-server/build/index.js was not found.");
            }
            catch (Exception e) { TerminalConfigBuilder.ReportMcpFailure(e.Message); }
            return new List<string>();
        }

        /// <summary>
        /// Primary source: agy's own ~/.gemini/antigravity-cli/history.jsonl, which records every USER
        /// prompt with {display, timestamp, workspace, conversationId} — readable, project-scoped, and
        /// grouped into real conversations (the AI's replies live in agy's binary store and stay
        /// there). Falls back to metadata-only stubs from our own sessions.jsonl.
        /// </summary>
        public override List<Conversation> LoadHistory(string projectRoot)
        {
            List<Conversation> rich = LoadFromAgyHistory(projectRoot);
            return rich.Count > 0 ? rich : SessionLog.LoadStubs(projectRoot, Id, "Antigravity session");
        }

        private List<Conversation> LoadFromAgyHistory(string projectRoot)
        {
            var byConv = new Dictionary<string, Conversation>();
            var order = new List<Conversation>();
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                // agy kept its Gemini-CLI predecessor's ".gemini" directory — this path is correct.
                string path = Path.Combine(home, ".gemini", "antigravity-cli", "history.jsonl");
                if (!File.Exists(path)) return order;

                string want = SessionLog.Norm(projectRoot);
                foreach (string raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    JObject o;
                    try { o = JObject.Parse(raw); } catch { continue; }
                    if (SessionLog.Norm((string)o["workspace"]) != want) continue;   // per-project isolation
                    string prompt = ((string)o["display"])?.Trim();
                    if (string.IsNullOrEmpty(prompt)) continue;
                    string convId = (string)o["conversationId"] ?? "?";
                    long ms = (long?)o["timestamp"] ?? 0;
                    DateTime ts = ms > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
                        : DateTime.MinValue;

                    if (!byConv.TryGetValue(convId, out Conversation conv))
                    {
                        conv = new Conversation
                        {
                            Title = SessionLog.Truncate(prompt, 60),
                            StartedAt = ts,
                            Agent = Id,
                        };
                        byConv[convId] = conv;
                        order.Add(conv);
                    }
                    conv.Turns.Add(new ConvTurn(TurnKind.You, prompt));
                }
            }
            catch { /* unreadable -> fall back to stubs */ }
            return order;
        }
    }
}
