using System;
using System.IO;

namespace AgenLink
{
    /// <summary>
    /// Locates Google's Antigravity CLI (<c>agy</c>) — a native Go TUI agent, the successor to the Gemini
    /// CLI. It's a normal console executable, so (like claude.exe) we launch it directly, no wrapper.
    /// </summary>
    internal static class AntigravityCli
    {
        /// <summary>Resolve <c>agy.exe</c>. Throws with an install hint if it can't be found.</summary>
        public static string ResolveExe()
        {
            // 1. Manual override from Settings.
            string custom = BridgeSettings.AntigravityPath;
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;

            // 2. Standard install location: %LOCALAPPDATA%\agy\bin\agy.exe
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string primary = Path.Combine(local, "agy", "bin", "agy.exe");
            if (File.Exists(primary)) return primary;

            // 3. PATH.
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string cand = Path.Combine(dir.Trim(), "agy.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { /* malformed PATH entry */ }
            }

            throw new Exception(
                "Could not find the Antigravity CLI (agy.exe). Install it from " +
                "https://antigravity.google/docs/cli-install, or set its path in Agen-Link ▸ Settings ▸ Antigravity CLI.");
        }

        /// <summary>Non-throwing description for the Settings label.</summary>
        public static string ResolveDisplay()
        {
            try { return ResolveExe(); }
            catch (Exception e) { return "(not found) " + e.Message; }
        }
    }
}
