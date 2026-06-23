using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using AgenLink.History;

namespace AgenLink.Analysis
{
    /// <summary>
    /// Per-project log of applied optimization fixes at AgenLink~/analysis.jsonl — one line per apply
    /// event, written by FixApplier from BOTH paths (the Analysis tab and the terminal/MCP bridge's
    /// agen_apply_fixes). The History tab surfaces each line as an amber ANALYSIS card. Best-effort
    /// like SessionLog: never throws, never blocks a fix.
    /// </summary>
    internal static class AnalysisLog
    {
        public static string PathFor(string projectRoot) => Path.Combine(projectRoot, "AgenLink~", "analysis.jsonl");

        /// <summary>Tests flip this so exercising FixApplier doesn't pollute the real project log.</summary>
        internal static bool Disabled;

        /// <summary>Record one apply event. source: "tab" | "bridge".</summary>
        public static void Append(string projectRoot, string source, IList<FixResult> results, bool sceneDirty)
        {
            if (Disabled) return;
            try
            {
                int ok = 0, failed = 0;
                var elems = new List<string>();
                foreach (FixResult r in results)
                {
                    if (r.Ok) ok++; else failed++;
                    elems.Add(r.ToJson());
                }
                string line = new JObj()
                    .S("ts", DateTime.UtcNow.ToString("o"))
                    .S("source", source)
                    .N("ok", ok)
                    .N("failed", failed)
                    .B("sceneDirty", sceneDirty)
                    .Raw("fixes", Json.Arr(elems))
                    .Build() + "\n";
                Directory.CreateDirectory(Path.Combine(projectRoot, "AgenLink~"));
                File.AppendAllText(PathFor(projectRoot), line, new UTF8Encoding(false));
            }
            catch { /* history is best-effort */ }
        }

        /// <summary>History cards for past fix applies. Pure file IO + Newtonsoft — safe off the GUI thread.</summary>
        public static List<Conversation> LoadConversations(string projectRoot)
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
                    if (!DateTime.TryParse((string)o["ts"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts)) continue;

                    var conv = new Conversation
                    {
                        Title = Title((int?)o["ok"] ?? 0, (int?)o["failed"] ?? 0),
                        StartedAt = ts.ToLocalTime(),
                        Agent = "analysis",
                        SourceNote = (string)o["source"] == "tab" ? "Analysis tab" : "Terminal/MCP",
                    };
                    if (o["fixes"] is JArray fixes)
                    {
                        foreach (JToken fix in fixes)
                        {
                            bool fixOk = (bool?)fix["ok"] ?? false;
                            bool permanent = (bool?)fix["permanent"] ?? false;
                            string text = (fixOk ? "✓ " : "✗ ") + (string)fix["type"] + " → " + (string)fix["target"]
                                          + (permanent ? "  [permanent]" : "");
                            string detail = (string)(fixOk ? fix["result"] : fix["error"]);
                            conv.Turns.Add(new ConvTurn(TurnKind.Action, text, detail));
                        }
                    }
                    list.Add(conv);
                }
            }
            catch { /* unreadable log -> no analysis cards, never break the tab */ }
            return list;
        }

        private static string Title(int ok, int failed)
        {
            string t = "Applied " + ok + (ok == 1 ? " fix" : " fixes");
            return failed > 0 ? t + " · " + failed + " failed" : t;
        }
    }
}
