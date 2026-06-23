using System.Text;

namespace AgenLink.History
{
    /// <summary>Converts a practical subset of Markdown into Unity IMGUI rich-text tags
    /// (&lt;b&gt;, &lt;i&gt;, &lt;color&gt;). Not a full CommonMark parser — it covers what Claude's
    /// replies actually use: headings, bold, italic, inline code, bullets, and fenced code.</summary>
    internal static class Markdown
    {
        public static string ToRichText(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var sb = new StringBuilder(md.Length + 32);
            string[] lines = md.Replace("\r\n", "\n").Split('\n');
            bool inFence = false;

            foreach (string raw in lines)
            {
                string trimmed = raw.TrimStart();

                if (trimmed.StartsWith("```"))
                {
                    inFence = !inFence;
                    continue;                                   // drop the ``` fence markers
                }
                if (inFence)
                {
                    sb.Append("<color=#9aa0b3>").Append(Escape(raw)).Append("</color>\n");
                    continue;
                }

                // ATX headings: #, ##, ### ...
                int h = 0;
                while (h < trimmed.Length && trimmed[h] == '#') h++;
                if (h > 0 && h <= 6 && h < trimmed.Length && trimmed[h] == ' ')
                {
                    sb.Append("<b>").Append(Inline(Escape(trimmed.Substring(h + 1)))).Append("</b>\n");
                    continue;
                }

                // bullets
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    sb.Append("   • ").Append(Inline(Escape(trimmed.Substring(2)))).Append('\n');
                    continue;
                }

                sb.Append(Inline(Escape(raw))).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // IMGUI rich-text has no escape for literal angle brackets, so swap them for look-alikes
        // to stop e.g. List<int> from being eaten as an unknown tag.
        private static string Escape(string s) => s.Replace("<", "‹").Replace(">", "›");

        private static string Inline(string s)
        {
            s = Pair(s, "**", "<b>", "</b>");
            s = Pair(s, "__", "<b>", "</b>");
            s = Pair(s, "`", "<color=#c6a0f6>", "</color>");
            s = Pair(s, "*", "<i>", "</i>");
            s = Pair(s, "_", "<i>", "</i>");
            return s;
        }

        // Replace paired delimiters left-to-right; close a dangling open at end-of-string.
        private static string Pair(string s, string delim, string open, string close)
        {
            var sb = new StringBuilder(s.Length);
            bool openNext = true;
            int i = 0;
            while (i < s.Length)
            {
                if (i + delim.Length <= s.Length && string.CompareOrdinal(s, i, delim, 0, delim.Length) == 0)
                {
                    sb.Append(openNext ? open : close);
                    openNext = !openNext;
                    i += delim.Length;
                }
                else sb.Append(s[i++]);
            }
            if (!openNext) sb.Append(close);
            return sb.ToString();
        }
    }
}
