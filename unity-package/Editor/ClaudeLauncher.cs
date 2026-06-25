using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgenLink
{
    /// <summary>Locates the Claude CLI executable.</summary>
    internal static class ClaudeCli
    {
        /// <summary>
        /// Resolve the real <c>claude.exe</c>. The npm global install ships a native exe at
        /// %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe (the claude.cmd shim just calls it).
        /// Launching the exe directly avoids any shell-quoting issues.
        /// </summary>
        public static string ResolveExe()
        {
            // Manual override from Settings.
            string custom = BridgeSettings.ClaudePath;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string primary = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (File.Exists(primary)) return primary;

            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "claude.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }

            throw new Exception(
                "Could not find claude.exe. Install Claude Code (npm i -g @anthropic-ai/claude-code) " +
                "or make sure it is on PATH.");
        }

        /// <summary>Non-throwing description for the Settings label.</summary>
        public static string ResolveDisplay()
        {
            try { return ResolveExe(); }
            catch (Exception e) { return "(not found) " + e.Message; }
        }
    }

    /// <summary>
    /// Builds the MCP config for each CLI (Claude: %TEMP% file passed via --mcp-config; Antigravity: the
    /// HOME-level config agy reads), generates the shared project-memory files, and resolves the MCP
    /// server path.
    /// </summary>
    internal static class ConfigBuilder
    {
        /// <summary>Absolute path to the open project's root (the folder that contains Assets/).</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        /// <summary>Find mcp-server/build/index.js (manual override first, then the package's sibling folder).</summary>
        public static string ResolveMcpServerPath()
        {
            string custom = BridgeSettings.McpServerPath;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;

            try
            {
                var pi = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeSettings).Assembly);
                if (pi != null && !string.IsNullOrEmpty(pi.resolvedPath))
                {
                    string candidate = Path.GetFullPath(Path.Combine(pi.resolvedPath, "..", "mcp-server", "build", "index.js"));
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { /* not installed as a package */ }

            return null;
        }

        public static string WriteMcpConfigTemp()
        {
            string mcpPath = ResolveMcpServerPath();
            if (mcpPath == null)
                throw new Exception(
                    "Could not find mcp-server/build/index.js. Build it (run install/setup.cmd or `npm run build` " +
                    "in mcp-server) and/or set the path in Agen-Link ▸ Settings.");

            string jsonPath = mcpPath.Replace("\\", "/");
            int port = BridgeSettings.Port;
            string root = ProjectRoot().Replace("\\", "/");
            // AGEN_LINK_PROJECT_ROOT + AGEN_LINK_CLI let the MCP server's memory tools locate the
            // shared store and tag who wrote each note. Same env goes to the Antigravity config below.
            string json = "{\"mcpServers\":{\"agenlink\":{\"command\":\"node\",\"args\":[" + Json.Str(jsonPath) +
                          "],\"env\":{\"AGEN_LINK_PORT\":" + Json.Str(port.ToString()) +
                          ",\"AGEN_LINK_PROJECT_ROOT\":" + Json.Str(root) +
                          ",\"AGEN_LINK_CLI\":" + Json.Str("claude") + "}}}}";

            // Per-project filename: two Editors open on different projects must not overwrite each
            // other's config between write and claude launch.
            string file = Path.Combine(Path.GetTempPath(), "agenlink-mcp-" + ProjectHash() + ".json");
            File.WriteAllText(file, json, new UTF8Encoding(false));
            return file;
        }

        /// <summary>Short stable hash of the open project's root, for per-project temp file names.</summary>
        private static string ProjectHash()
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ProjectRoot().ToLowerInvariant()));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Configure the Unity MCP bridge for the Antigravity CLI (agy). agy reads MCP servers from the
        /// HOME-level <c>~/.gemini/config/mcp_config.json</c> (project-local config is unreliable). We merge
        /// our `agenlink` server in, preserving any other servers, and rewrite it on each Start so the port /
        /// project root stay current. agy auto-loads the project's AGENTS.md natively, so no context wiring is
        /// needed here. No-op (returns) if the MCP server isn't built — agy still launches, just without Unity
        /// awareness.
        /// </summary>
        public static void WriteAntigravityMcpConfig()
        {
            string mcpPath = ResolveMcpServerPath();
            if (mcpPath == null) return;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // agy kept its Gemini-CLI predecessor's ".gemini" directory — this path is correct, don't "fix" it.
            string dir = Path.Combine(home, ".gemini", "config");
            Directory.CreateDirectory(dir);
            string cfgPath = Path.Combine(dir, "mcp_config.json");

            JObject cfg;
            try
            {
                cfg = File.Exists(cfgPath) && new FileInfo(cfgPath).Length > 0
                    ? JObject.Parse(File.ReadAllText(cfgPath))
                    : new JObject();
            }
            catch { cfg = new JObject(); } // corrupt/partial file — start fresh rather than fail the launch

            if (!(cfg["mcpServers"] is JObject servers)) { servers = new JObject(); cfg["mcpServers"] = servers; }
            servers["agenlink"] = new JObject
            {
                ["command"] = "node",
                ["args"] = new JArray(mcpPath.Replace("\\", "/")),
                ["env"] = new JObject
                {
                    ["AGEN_LINK_PORT"] = BridgeSettings.Port.ToString(),
                    ["AGEN_LINK_PROJECT_ROOT"] = ProjectRoot().Replace("\\", "/"),
                    ["AGEN_LINK_CLI"] = "antigravity",
                },
            };

            File.WriteAllText(cfgPath, cfg.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }

        /// <summary>
        /// Create the shared, model-agnostic project memory files in the OPEN project root (local/gitignored):
        /// AGENTS.md (always-in-context; Claude imports it via CLAUDE.md, agy reads it natively and also honors
        /// GEMINI.md), plus thin CLAUDE.md/GEMINI.md that import it, and a .gitignore block. Non-destructive:
        /// never overwrites an existing AGENTS.md, and only appends the import line to an existing
        /// CLAUDE.md/GEMINI.md.
        /// </summary>
        public static void EnsureProjectMemoryFiles()
        {
            string root = ProjectRoot();
            var enc = new UTF8Encoding(false);

            string agents = Path.Combine(root, "AGENTS.md");
            if (!File.Exists(agents)) File.WriteAllText(agents, BuildAgentsSeed(), enc);

            EnsureImports(Path.Combine(root, "CLAUDE.md"), enc);
            EnsureImports(Path.Combine(root, "GEMINI.md"), enc);
            EnsureGitignore(root, enc);
        }

        private static void EnsureImports(string path, UTF8Encoding enc)
        {
            const string import = "@AGENTS.md";
            if (!File.Exists(path))
            {
                File.WriteAllText(path,
                    "# Project memory\n\nShared, always-in-context project memory lives in AGENTS.md (read by both " +
                    "the Claude and Antigravity CLIs):\n\n" + import + "\n", enc);
                return;
            }
            if (!File.ReadAllText(path).Contains("AGENTS.md"))
                File.AppendAllText(path, "\n" + import + "\n", enc);
        }

        private static void EnsureGitignore(string root, UTF8Encoding enc)
        {
            string path = Path.Combine(root, ".gitignore");
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            const string header = "# ----- Agen-Link (local, do not commit) -----";
            string[] entries = { "AgenLink~/", ".gemini/", "AGENTS.md", "CLAUDE.md", "GEMINI.md" };

            var add = new StringBuilder();
            if (!existing.Contains(header)) add.Append('\n').Append(header).Append('\n');
            foreach (var e in entries)
            {
                string pat = "(?m)^" + System.Text.RegularExpressions.Regex.Escape(e) + @"\s*$";
                if (!System.Text.RegularExpressions.Regex.IsMatch(existing, pat)) add.Append(e).Append('\n');
            }
            if (add.Length > 0) File.AppendAllText(path, add.ToString(), enc);
        }

        private static string BuildAgentsSeed()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            string rpName = rp != null ? rp.GetType().Name : "Built-in Render Pipeline";
            var sb = new StringBuilder();
            sb.Append("# Project memory (Agen-Link, shared)\n\n");
            sb.Append("> Local & gitignored. Read by BOTH the Claude and Antigravity CLIs launched from the Unity\n");
            sb.Append("> \"Agen-Link\" terminal. Record durable knowledge here so the other CLI does not have to\n");
            sb.Append("> re-scan the project from scratch.\n\n");
            sb.Append("## This project\n\n");
            sb.Append("- Unity ").Append(Application.unityVersion).Append('\n');
            sb.Append("- Product: ").Append(Application.productName).Append('\n');
            sb.Append("- Render pipeline: ").Append(rpName).Append("\n\n");
            sb.Append("## How to work here\n\n");
            sb.Append("- BEFORE scanning the project, call `agen_memory_search` to reuse what the other CLI already learned.\n");
            sb.Append("- Use the `agen_*` MCP tools for live editor state (project info, console, compile errors, scene, assets, code graph).\n");
            sb.Append("- When you learn something durable (an architecture decision, a gotcha, where a system lives),\n");
            sb.Append("  record it with `agen_memory_append` so the other CLI inherits it.\n");
            sb.Append("- After editing C# scripts: `agen_refresh_assets`, then poll `agen_get_compile_errors` and fix any errors.\n");
            sb.Append("- Scene optimization: run `agen_audit_scene` + `agen_audit_assets` (structured findings), then\n");
            sb.Append("  `agen_perf_start` -> poll `agen_perf_status` -> `agen_perf_report` for play-mode numbers. Report\n");
            sb.Append("  findings to the user, apply agreed fixes via `agen_apply_fixes` (scene fixes are Undo-able and\n");
            sb.Append("  unsaved — the user reviews and saves), then RE-RUN audit+profile and show before/after numbers.\n\n");
            sb.Append("## Notes\n\n(Durable project notes accumulate here and in the shared memory store.)\n");
            return sb.ToString();
        }
    }
}
