using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AgenLink.History
{
    /// <summary>
    /// Per-project log of terminal sessions Agen-Link launched (which CLI, when), at
    /// AgenLink~/sessions.jsonl. Claude sessions have full transcripts (TranscriptReader), but
    /// Antigravity and Codex keep their conversation content in their own stores — this log is what
    /// lets the History tab still list those sessions as metadata-only cards.
    /// </summary>
    internal static class SessionLog
    {
        public static string PathFor(string projectRoot) => Path.Combine(projectRoot, "AgenLink~", "sessions.jsonl");

        /// <summary>Record a terminal session start. Best-effort: never blocks or fails a launch.</summary>
        public static void Append(string projectRoot, string cli)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(projectRoot, "AgenLink~"));
                string line = "{\"cli\":" + Json.Str(cli) + ",\"ts\":" + Json.Str(DateTime.UtcNow.ToString("o")) + "}\n";
                File.AppendAllText(PathFor(projectRoot), line, new UTF8Encoding(false));
            }
            catch { /* history is best-effort */ }
        }

        /// <summary>
        /// Metadata-only cards for one CLI, from our own sessions.jsonl. The fallback whenever a CLI's
        /// own history file is missing or unreadable: we still know the session happened.
        /// </summary>
        public static List<Conversation> LoadStubs(string projectRoot, string cliId, string title)
        {
            var list = new List<Conversation>();
            try
            {
                string path = PathFor(projectRoot);
                if (!File.Exists(path)) return list;
                foreach (string raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    JObject o;
                    try { o = JObject.Parse(raw); } catch { continue; }
                    if ((string)o["cli"] != cliId) continue;
                    if (!DateTime.TryParse((string)o["ts"], CultureInfo.InvariantCulture,
                                           DateTimeStyles.RoundtripKind, out DateTime ts)) continue;
                    list.Add(new Conversation
                    {
                        Title = title,
                        StartedAt = ts.ToLocalTime(),
                        Agent = cliId,
                        MetaOnly = true,
                    });
                }
            }
            catch { /* unreadable log -> no cards, never break the tab */ }
            return list;
        }

        /// <summary>Path comparison key: separators and case normalized, trailing slash dropped.</summary>
        internal static string Norm(string p) =>
            (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        /// <summary>Single-line, length-capped title text.</summary>
        internal static string Truncate(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
