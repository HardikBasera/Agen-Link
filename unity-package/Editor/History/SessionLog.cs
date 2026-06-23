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
    /// Antigravity stores its conversation content in its own binary format — this log is what lets
    /// the History tab still list agy sessions as metadata-only cards.
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
        /// Antigravity conversations for this project. Primary source: agy's own
        /// ~/.gemini/antigravity-cli/history.jsonl, which records every USER prompt with
        /// {display, timestamp, workspace, conversationId} — readable, project-scoped, grouped into
        /// real conversations (the AI's replies live in agy's binary store and stay there). Fallback
        /// when that file yields nothing: metadata-only stubs from our own sessions.jsonl.
        /// </summary>
        public static List<Conversation> LoadAntigravity(string projectRoot)
        {
            List<Conversation> rich = LoadFromAgyHistory(projectRoot);
            return rich.Count > 0 ? rich : LoadSessionStubs(projectRoot);
        }

        private static List<Conversation> LoadFromAgyHistory(string projectRoot)
        {
            var byConv = new Dictionary<string, Conversation>();
            var order = new List<Conversation>();
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string path = Path.Combine(home, ".gemini", "antigravity-cli", "history.jsonl");
                if (!File.Exists(path)) return order;

                string want = Norm(projectRoot);
                foreach (string raw in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    JObject o;
                    try { o = JObject.Parse(raw); } catch { continue; }
                    if (Norm((string)o["workspace"]) != want) continue;        // per-project isolation
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
                            Title = Truncate(prompt, 60),
                            StartedAt = ts,
                            Agent = "antigravity",
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

        private static List<Conversation> LoadSessionStubs(string projectRoot)
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
                    if ((string)o["cli"] != "antigravity") continue;
                    if (!DateTime.TryParse((string)o["ts"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts)) continue;
                    list.Add(new Conversation
                    {
                        Title = "Antigravity session",
                        StartedAt = ts.ToLocalTime(),
                        Agent = "antigravity",
                        MetaOnly = true,
                    });
                }
            }
            catch { /* unreadable log -> no agy cards, never break the tab */ }
            return list;
        }

        private static string Norm(string p) =>
            (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        private static string Truncate(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
