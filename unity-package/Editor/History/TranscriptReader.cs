using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AgenLink.History
{
    /// <summary>Locates and parses Claude Code session transcripts (JSONL) for the current project.</summary>
    internal static class TranscriptReader
    {
        /// <summary>Claude encodes a project's cwd into its transcript dir name by replacing every
        /// non-alphanumeric character with '-'. e.g. D:\Unity Project\Foo -> D--Unity-Project-Foo.</summary>
        public static string EncodeProjectDir(string projectRoot)
        {
            var sb = new StringBuilder(projectRoot.Length);
            foreach (char c in projectRoot) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
        }

        public static string ProjectsDir(string projectRoot)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".claude", "projects", EncodeProjectDir(projectRoot));
        }

        /// <summary>Load every session transcript for the project, newest first. Never throws.</summary>
        public static List<Conversation> LoadAll(string projectRoot)
        {
            var list = new List<Conversation>();
            string dir = ProjectsDir(projectRoot);
            if (!Directory.Exists(dir)) return list;

            foreach (string file in Directory.GetFiles(dir, "*.jsonl"))
            {
                try
                {
                    var conv = ParseConversation(File.ReadLines(file));
                    if (conv.Turns.Count == 0) continue;
                    conv.FilePath = file;
                    if (conv.StartedAt == DateTime.MinValue) conv.StartedAt = File.GetLastWriteTime(file);
                    list.Add(conv);
                }
                catch { /* skip a transcript we can't read or parse */ }
            }
            list.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            return list;
        }

        public static Conversation ParseConversation(IEnumerable<string> lines)
        {
            var turns = new List<ConvTurn>();
            string aiTitle = null, firstPrompt = null;
            DateTime startedAt = DateTime.MinValue;

            foreach (string raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                JObject o;
                try { o = JObject.Parse(raw); } catch { continue; }

                string type = (string)o["type"];
                if (type == "ai-title") { aiTitle = (string)o["aiTitle"]; continue; }
                if (type != "user" && type != "assistant") continue;
                if ((bool?)o["isMeta"] == true) continue;

                JToken msg = o["message"];
                if (msg == null) continue;
                if (startedAt == DateTime.MinValue) startedAt = ParseTimestamp(o["timestamp"]);

                if (type == "user")
                {
                    string text = ExtractUserText(msg["content"]);
                    if (text == null) continue;                 // tool_result echo or empty -> not a prompt
                    if (firstPrompt == null) firstPrompt = text;
                    turns.Add(new ConvTurn(TurnKind.You, text));
                }
                else
                {
                    if (!(msg["content"] is JArray content)) continue;
                    foreach (JToken blk in content)
                    {
                        string bt = (string)blk["type"];
                        if (bt == "text")
                        {
                            string t = (string)blk["text"];
                            if (!string.IsNullOrWhiteSpace(t)) turns.Add(new ConvTurn(TurnKind.Claude, t.Trim()));
                        }
                        else if (bt == "tool_use")
                        {
                            ConvTurn action = ActionFor((string)blk["name"], blk["input"] as JObject);
                            if (action != null) turns.Add(action);
                        }
                        // "thinking" blocks intentionally dropped
                    }
                }
            }

            string title = !string.IsNullOrWhiteSpace(aiTitle)
                ? aiTitle
                : Truncate(firstPrompt ?? "(untitled session)", 60);
            return new Conversation { Title = title, StartedAt = startedAt, Turns = turns };
        }

        private static string ExtractUserText(JToken content)
        {
            if (content == null) return null;
            if (content.Type == JTokenType.String)
            {
                string s = ((string)content).Trim();
                return s.Length == 0 ? null : s;
            }
            if (content is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (JToken b in arr)
                {
                    string bt = (string)b["type"];
                    if (bt == "tool_result") return null;       // result echo, not a typed prompt
                    if (bt == "text") sb.AppendLine((string)b["text"]);
                }
                string s = sb.ToString().Trim();
                return s.Length == 0 ? null : s;
            }
            return null;
        }

        /// <summary>Short marker for the list plus the full, untruncated detail (path / command) so the
        /// UI can offer an expandable view. Read-only / noise tools stay hidden.</summary>
        private static ConvTurn ActionFor(string name, JObject input)
        {
            switch (name)
            {
                case "Write":        return Action("✎ wrote "  + FileName(input, "file_path"), (string)input?["file_path"]);
                case "Edit":
                case "MultiEdit":    return Action("✎ edited " + FileName(input, "file_path"), (string)input?["file_path"]);
                case "NotebookEdit": return Action("✎ edited " + FileName(input, "notebook_path"), (string)input?["notebook_path"]);
                case "Bash":
                {
                    string cmd = (string)input?["command"] ?? "";
                    string desc = (string)input?["description"];
                    string detail = string.IsNullOrWhiteSpace(desc) ? cmd : desc + "\n" + cmd;
                    return Action("▶ ran: " + Truncate(cmd, 60), detail);
                }
                default: return null;
            }
        }

        private static ConvTurn Action(string marker, string detail)
        {
            detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
            // No expandable row when the detail adds nothing over the marker itself.
            if (detail != null && marker.EndsWith(detail, StringComparison.Ordinal)) detail = null;
            return new ConvTurn(TurnKind.Action, marker, detail);
        }

        private static string FileName(JObject input, string key)
        {
            string p = (string)input?[key];
            return string.IsNullOrEmpty(p) ? "(file)" : Path.GetFileName(p);
        }

        private static DateTime ParseTimestamp(JToken t)
        {
            string s = (string)t;
            if (!string.IsNullOrEmpty(s) &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dt))
                return dt.ToLocalTime();
            return DateTime.MinValue;
        }

        private static string Truncate(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
