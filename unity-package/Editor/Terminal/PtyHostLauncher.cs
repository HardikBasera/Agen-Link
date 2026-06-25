using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AgenLink.Terminal
{
    /// <summary>Locates node + the pty-host, launches the detached broker, and tracks it across domain reloads.</summary>
    internal static class PtyHostLauncher
    {
        public static string ResolveNodeExe()
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { string p = Path.Combine(dir.Trim(), "node.exe"); if (File.Exists(p)) return p; }
                catch { /* malformed PATH entry */ }
            }
            foreach (var c in new[] { @"C:\Program Files\nodejs\node.exe" })
                if (File.Exists(c)) return c;
            return "node";
        }

        /// <summary>pty-host/index.js, found as a sibling of the package (same approach as the MCP server).</summary>
        public static string ResolvePtyHostEntry()
        {
            try
            {
                var pi = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeSettings).Assembly);
                if (pi != null && !string.IsNullOrEmpty(pi.resolvedPath))
                {
                    string cand = Path.GetFullPath(Path.Combine(pi.resolvedPath, "..", "pty-host", "index.js"));
                    if (File.Exists(cand)) return cand;
                }
            }
            catch { /* not installed as a package */ }
            return null;
        }

        public static bool HostAlive()
        {
            int pid = BridgeSettings.TerminalHostPid;
            if (pid == 0) return false;
            try { var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch { return false; }
        }

        /// <summary>Start a new host. On success, port/token are stored in BridgeSettings (SessionState).</summary>
        public static bool Start(int cols, int rows, out string error)
        {
            error = null;
            string node = ResolveNodeExe();
            string entry = ResolvePtyHostEntry();
            if (entry == null) { error = "pty-host not found. Run install/setup.cmd to build it."; return false; }

            // Shared project memory files for whichever CLI runs (AGENTS.md + CLAUDE.md/GEMINI.md + .gitignore).
            try { ConfigBuilder.EnsureProjectMemoryFiles(); } catch { /* non-fatal: launch anyway */ }

            // Pick the CLI: claude.exe or agy.exe (Antigravity) — both native console exes. Antigravity's
            // Unity bridge comes from ~/.gemini/config/mcp_config.json we write here (it has no --mcp-config
            // flag like Claude).
            string cmd;
            var args = new System.Collections.Generic.List<string>();
            try
            {
                if (BridgeSettings.TerminalCli == "antigravity")
                {
                    cmd = AntigravityCli.ResolveExe();
                    try { ConfigBuilder.WriteAntigravityMcpConfig(); } catch { /* bridge optional — agy still runs */ }
                    args.AddRange(TerminalConfigBuilder.BuildAntigravityArgs());
                }
                else
                {
                    cmd = ClaudeCli.ResolveExe();
                    args.AddRange(TerminalConfigBuilder.BuildClaudeArgs());
                }
            }
            catch (Exception e) { error = e.Message; return false; }

            string token = Guid.NewGuid().ToString("N");

            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = "\"" + entry + "\"",
                WorkingDirectory = ConfigBuilder.ProjectRoot(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.EnvironmentVariables["UBPTY_TOKEN"] = token;
            psi.EnvironmentVariables["UBPTY_CMD"] = cmd;
            psi.EnvironmentVariables["UBPTY_ARGS"] = SerializeArgs(args);
            psi.EnvironmentVariables["UBPTY_CWD"] = ConfigBuilder.ProjectRoot();
            psi.EnvironmentVariables["UBPTY_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString();
            psi.EnvironmentVariables["UBPTY_COLS"] = cols.ToString();
            psi.EnvironmentVariables["UBPTY_ROWS"] = rows.ToString();

            Process proc;
            try { proc = Process.Start(psi); }
            catch (Exception e) { error = "Failed to start node: " + e.Message; return false; }

            // Read the chosen port from stdout (blocks briefly until the host reports it).
            int port = 0;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                string line = proc.StandardOutput.ReadLine();
                if (line == null) break;
                int idx = line.IndexOf("UBPTY_PORT=", StringComparison.Ordinal);
                if (idx >= 0 && int.TryParse(line.Substring(idx + "UBPTY_PORT=".Length).Trim(), out port)) break;
            }
            if (port == 0)
            {
                error = "pty-host did not report a port (is node-pty installed? run install/setup.cmd).";
                try { proc.Kill(); } catch { }
                return false;
            }

            BridgeSettings.TerminalHostPid = proc.Id;
            BridgeSettings.TerminalHostPort = port;
            BridgeSettings.TerminalHostToken = token;
            // Session log feeds the History tab (Antigravity content isn't parseable, but the session is).
            History.SessionLog.Append(ConfigBuilder.ProjectRoot(),
                BridgeSettings.TerminalCli == "antigravity" ? "antigravity" : "claude");
            return true;
        }

        public static void Stop()
        {
            int pid = BridgeSettings.TerminalHostPid;
            if (pid != 0)
            {
                try { var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(); } catch { }
            }
            BridgeSettings.TerminalHostPid = 0;
            BridgeSettings.TerminalHostPort = 0;
            BridgeSettings.TerminalHostToken = "";
        }

        // JSON array of strings (matches JSON.parse on the node side).
        private static string SerializeArgs(System.Collections.Generic.List<string> args)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(args[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            return sb.Append(']').ToString();
        }
    }
}
