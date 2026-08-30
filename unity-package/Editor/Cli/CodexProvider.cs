using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using AgenLink.History;
using AgenLink.Terminal;

namespace AgenLink.Cli
{
    /// <summary>
    /// OpenAI's Codex CLI. Its Unity bridge is injected as -c/--config TOML overrides at launch rather
    /// than by editing ~/.codex/config.toml: CLI overrides sit at the TOP of Codex's precedence chain
    /// (flags > project .codex/ > profile > user config > system > defaults), so nothing can shadow
    /// them, the port and project root are current by construction, and we never touch a file the user
    /// owns. Project-local .codex/config.toml was rejected because Codex loads project layers only for
    /// TRUSTED projects — on first run it would silently do nothing.
    /// Codex reads AGENTS.md natively, so no import file is needed (unlike CLAUDE.md / GEMINI.md).
    /// </summary>
    internal sealed class CodexProvider : CliProvider
    {
        /// <summary>The MCP server id Codex will show these tools under.</summary>
        private const string ServerId = "agenlink";

        public override string Id => "codex";
        public override string DisplayName => "Codex";
        public override string SettingsHeading => "Codex CLI";
        public override string ExeFileName => "codex.exe";
        public override string AutoDetectHint =>
            "Auto-detected from npm global / the Windows installer / PATH. Set only if Codex isn't found.";
        public override string ResumeHint =>
            "Reopen it with \u201ccodex resume\u201d in the Terminal.";
        public override Color AccentColor => new Color(0x6E / 255f, 0xC1 / 255f, 0x7C / 255f); // green

        public override string PathOverride
        {
            get => BridgeSettings.CodexPath;
            set => BridgeSettings.CodexPath = value;
        }

        public override string ResolveExe()
        {
            string custom = PathOverride;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string npmRoot = Path.Combine(appData, "npm", "node_modules");

            // The npm package's own bin/ holds only a JS shim (bin/codex.js) — VERIFIED against
            // @openai/codex 0.151.0, where this path does NOT exist. Kept in case a future release
            // ships a real exe there.
            string npmBin = Path.Combine(npmRoot, "@openai", "codex", "bin", "codex.exe");
            if (File.Exists(npmBin)) return npmBin;

            // The native binary ships in a per-platform package, @openai/codex-win32-<arch>, under
            // vendor/<target triple>/bin. npm 11 nests that inside the codex package; other npm
            // versions hoist it to the global root, and the triple differs on arm64 — so enumerate
            // both parents rather than hardcoding one layout.
            foreach (string parent in new[]
                     {
                         Path.Combine(npmRoot, "@openai", "codex", "node_modules", "@openai"),
                         Path.Combine(npmRoot, "@openai"),
                     })
            {
                string vendored = FindVendoredExe(parent);
                if (vendored != null) return vendored;
            }

            // Standalone Windows installer. These candidates are unverified — npm is the documented
            // route and the only one confirmed live.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string candidate in new[]
                     {
                         Path.Combine(local, "Programs", "codex", "codex.exe"),
                         Path.Combine(local, "codex", "bin", "codex.exe"),
                         Path.Combine(programFiles, "Codex", "codex.exe"),
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }

            string onPath = ScanPath(ExeFileName);
            if (onPath != null) return onPath;

            // npm puts codex.cmd/codex (shell shims) on PATH, never codex.exe. pty-host spawns an argv
            // array through CreateProcess, which cannot execute a .cmd, so a shim is not a usable
            // answer — say so, or "codex runs in my terminal but Agen-Link cannot find it" is baffling.
            string shim = ScanPath("codex.cmd");
            if (shim != null)
            {
                throw new Exception(
                    "Found the npm shim at " + shim + " but not codex.exe itself. The real binary lives " +
                    "under @openai/codex-win32-x64/vendor/<triple>/bin — point Agen-Link \u25b8 Settings " +
                    "\u25b8 Codex CLI at it.");
            }

            throw new Exception(
                "Could not find codex.exe. Install the Codex CLI (npm i -g @openai/codex, or the Windows " +
                "installer from https://developers.openai.com/codex/cli), or set its path in " +
                "Agen-Link \u25b8 Settings \u25b8 Codex CLI.");
        }

        /// <summary>
        /// Look for &lt;parent&gt;/codex-win32-*/vendor/&lt;triple&gt;/bin/codex.exe — the layout the npm
        /// package uses to ship its native binary.
        /// </summary>
        private string FindVendoredExe(string parent)
        {
            try
            {
                if (!Directory.Exists(parent)) return null;
                foreach (string platformPkg in Directory.GetDirectories(parent, "codex-win32-*"))
                {
                    string vendor = Path.Combine(platformPkg, "vendor");
                    if (!Directory.Exists(vendor)) continue;
                    foreach (string triple in Directory.GetDirectories(vendor))
                    {
                        string exe = Path.Combine(triple, "bin", ExeFileName);
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { /* unreadable node_modules */ }
            return null;
        }

        /// <summary>
        /// Build the -c overrides that register the Unity MCP server for this session.
        /// default_tools_approval_mode="auto" auto-approves OUR tools only — Codex's own sandbox and
        /// --ask-for-approval defaults are deliberately left untouched.
        /// </summary>
        public override List<string> BuildArgs()
        {
            var args = new List<string>();
            try
            {
                // -c/--config is core, documented Codex CLI surface, so it is used unconditionally.
                // Deliberately NOT sniffed via --help: that would spawn a process on every launch and
                // make this method require an installed Codex, which no unit test should need.
                string mcpPath = ConfigBuilder.ResolveMcpServerPath();
                if (mcpPath == null)
                {
                    TerminalConfigBuilder.ReportMcpFailure("mcp-server/build/index.js was not found.");
                    return args;
                }

                string prefix = "mcp_servers." + ServerId + ".";
                Add(args, prefix + "command", Toml.Str("node"));
                Add(args, prefix + "args", Toml.StrArray(mcpPath.Replace("\\", "/")));
                Add(args, prefix + "env", Toml.InlineTable(
                    // AGEN_LINK_PROJECT_ROOT + AGEN_LINK_CLI let the MCP server's memory tools locate
                    // the shared store and tag who wrote each note.
                    new KeyValuePair<string, string>("AGEN_LINK_PORT", BridgeSettings.Port.ToString()),
                    new KeyValuePair<string, string>("AGEN_LINK_PROJECT_ROOT", ConfigBuilder.ProjectRoot().Replace("\\", "/")),
                    new KeyValuePair<string, string>("AGEN_LINK_CLI", Id)));
                Add(args, prefix + "default_tools_approval_mode", Toml.Str("auto"));
                Add(args, prefix + "startup_timeout_sec", "30");

                // AGENTS.md already carries the hard rules; this is the extra steering Claude gets via
                // --append-system-prompt.
                Add(args, "developer_instructions", Toml.Str(SystemPrompt));

                LaunchDiagnostics.McpFailure = null;
            }
            catch (Exception e)
            {
                TerminalConfigBuilder.ReportMcpFailure(e.Message);
                args.Clear();
            }
            return args;
        }

        /// <summary>One override = two argv entries. pty-host spawns an argv array, so no shell
        /// quoting is involved and the value never gets split on spaces.</summary>
        private static void Add(List<string> args, string key, string tomlValue)
        {
            args.Add("-c");
            args.Add(key + "=" + tomlValue);
        }

        /// <summary>
        /// Codex records prompts in $CODEX_HOME/history.jsonl (default ~/.codex) when history
        /// persistence is on. The schema is not contractual, so every field is probed leniently and
        /// any failure falls back to metadata-only stubs from our own sessions.jsonl.
        /// </summary>
        public override List<Conversation> LoadHistory(string projectRoot)
        {
            List<Conversation> rich = LoadFromCodexHistory(projectRoot);
            return rich.Count > 0 ? rich : SessionLog.LoadStubs(projectRoot, Id, "Codex session");
        }

        private List<Conversation> LoadFromCodexHistory(string projectRoot)
        {
            var byConv = new Dictionary<string, Conversation>();
            var order = new List<Conversation>();
            try
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrEmpty(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string path = Path.Combine(home, "history.jsonl");
                if (!File.Exists(path)) return order;

                string want = SessionLog.Norm(projectRoot);
                foreach (string raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    Newtonsoft.Json.Linq.JObject o;
                    try { o = Newtonsoft.Json.Linq.JObject.Parse(raw); } catch { continue; }

                    string cwd = First(o, "cwd", "workspace", "project", "project_root");
                    // No recognisable cwd means we cannot prove the record belongs to this project.
                    // Skip it: the project-scoped sessions.jsonl stub fallback is the reliable path, and
                    // keeping unattributable records would both mis-attribute them and suppress that fallback.
                    if (string.IsNullOrEmpty(cwd) || SessionLog.Norm(cwd) != want) continue;

                    string prompt = First(o, "text", "display", "prompt", "message")?.Trim();
                    if (string.IsNullOrEmpty(prompt)) continue;

                    string convId = First(o, "session_id", "conversationId", "conversation_id", "id") ?? "?";
                    DateTime ts = ParseTimestamp(o);

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

        private static string First(Newtonsoft.Json.Linq.JObject o, params string[] names)
        {
            foreach (string n in names)
            {
                var token = o[n];
                if (token != null && token.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    string s = (string)token;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return null;
        }

        private static DateTime ParseTimestamp(Newtonsoft.Json.Linq.JObject o)
        {
            foreach (string n in new[] { "ts", "timestamp", "created_at", "time" })
            {
                var token = o[n];
                if (token == null) continue;
                if (token.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                {
                    long v = (long)token;
                    // Seconds vs milliseconds since epoch.
                    return v > 100000000000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(v).LocalDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime;
                }
                if (DateTime.TryParse((string)token, out DateTime parsed)) return parsed.ToLocalTime();
            }
            return DateTime.MinValue;
        }
    }
}
