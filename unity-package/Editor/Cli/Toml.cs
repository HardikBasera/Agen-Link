using System.Collections.Generic;
using System.Text;

namespace AgenLink.Cli
{
    /// <summary>
    /// Just enough TOML to build Codex <c>-c key=value</c> overrides. Codex parses each --config
    /// value as TOML (and falls back to treating it as a string if it does not parse), so every
    /// value we emit is quoted/bracketed explicitly rather than left to that fallback.
    /// </summary>
    internal static class Toml
    {
        /// <summary>A TOML basic string: quoted, with backslash/quote/whitespace escapes.</summary>
        public static string Str(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        // TOML basic strings forbid the control characters and DEL outright, and they
                        // have no escape here. A NUL in particular would truncate any Unity log that
                        // later embeds this value, so drop the whole class.
                        if (c >= ' ' && c != (char)0x7f) sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>A TOML array of basic strings: <c>["a","b"]</c>.</summary>
        public static string StrArray(params string[] values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Str(values[i]));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>A TOML inline table of string values: <c>{A="1",B="2"}</c>.</summary>
        public static string InlineTable(params KeyValuePair<string, string>[] pairs)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < pairs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(pairs[i].Key).Append('=').Append(Str(pairs[i].Value));
            }
            return sb.Append('}').ToString();
        }
    }
}
