using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgenLink
{
    /// <summary>
    /// Minimal, allocation-light JSON writer. We build response strings ourselves so we have full control
    /// over shape (including recursive scene dumps) and avoid any third-party JSON dependency. Request
    /// parsing is handled separately by Unity's JsonUtility against a flat DTO.
    /// </summary>
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Wrap a list of already-serialized element strings into a JSON array.</summary>
        public static string Arr(IEnumerable<string> elements)
        {
            return "[" + string.Join(",", elements) + "]";
        }
    }

    /// <summary>Tiny fluent builder for a single JSON object. Call Build() once.</summary>
    internal sealed class JObj
    {
        private readonly StringBuilder _sb = new StringBuilder("{");
        private bool _first = true;

        private void Key(string k)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            _sb.Append(Json.Str(k)).Append(':');
        }

        public JObj S(string k, string v) { Key(k); _sb.Append(Json.Str(v)); return this; }
        public JObj N(string k, long v) { Key(k); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JObj B(string k, bool v) { Key(k); _sb.Append(v ? "true" : "false"); return this; }

        /// <summary>Add a key whose value is already-serialized JSON (object, array, number, etc.).</summary>
        public JObj Raw(string k, string rawJson) { Key(k); _sb.Append(rawJson); return this; }

        public string Build() { _sb.Append('}'); return _sb.ToString(); }
    }
}
