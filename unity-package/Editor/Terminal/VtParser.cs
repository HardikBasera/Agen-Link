using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AgenLink.Terminal
{
    /// <summary>Focused ANSI/VT interpreter: SGR, cursor moves, erase, alt-screen, modes, OSC (ignored).</summary>
    internal sealed class VtParser
    {
        private enum S { Ground, Esc, Csi, Osc }
        private readonly ScreenBuffer _sb;
        private S _state = S.Ground;
        private readonly StringBuilder _csi = new StringBuilder();
        private readonly System.Text.Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly char[] _one = new char[2];

        // 16-color ANSI palette (normal + bright).
        private static readonly Color32[] Palette = BuildPalette();

        public VtParser(ScreenBuffer sb) { _sb = sb; }

        public void Feed(byte[] bytes) { Feed(bytes, bytes.Length); }

        public void Feed(byte[] bytes, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[i];
                switch (_state)
                {
                    case S.Ground: Ground(b); break;
                    case S.Esc: Esc(b); break;
                    case S.Csi: Csi(b); break;
                    case S.Osc: Osc(b); break;
                }
            }
        }

        private void Ground(byte b)
        {
            if (b == 0x1b) { _state = S.Esc; return; }
            if (b == '\r') { _sb.CarriageReturn(); return; }
            if (b == '\n') { _sb.LineFeed(); return; }
            if (b == '\b') { _sb.Backspace(); return; }
            if (b == '\t') { _sb.Tab(); return; }
            if (b < 0x20) return; // ignore other C0
            // Decode (possibly multi-byte) UTF-8 to a char.
            int used = _utf8.GetChars(new[] { b }, 0, 1, _one, 0);
            for (int k = 0; k < used; k++) _sb.Put(_one[k]);
        }

        private void Esc(byte b)
        {
            if (b == '[') { _csi.Clear(); _state = S.Csi; return; }
            if (b == ']') { _csi.Clear(); _state = S.Osc; return; }
            if (b == '(' || b == ')') { _state = S.Ground; return; } // charset select — ignore
            _state = S.Ground;
        }

        private void Csi(byte b)
        {
            if ((b >= '0' && b <= '9') || b == ';' || b == '?') { _csi.Append((char)b); return; }
            if (b >= 0x40 && b <= 0x7e) { Dispatch((char)b, _csi.ToString()); _state = S.Ground; return; }
            // ignore intermediates
        }

        private void Osc(byte b)
        {
            if (b == 0x07) { _state = S.Ground; return; }          // BEL terminator
            if (b == 0x1b) { _state = S.Esc; return; }             // ST begins with ESC
            // swallow title bytes
        }

        private void Dispatch(char final, string raw)
        {
            bool priv = raw.StartsWith("?");
            string body = priv ? raw.Substring(1) : raw;
            var ps = ParseParams(body);

            switch (final)
            {
                case 'H': case 'f': _sb.MoveTo(Get(ps, 0, 1) - 1, Get(ps, 1, 1) - 1); break;
                case 'A': _sb.MoveBy(-Get(ps, 0, 1), 0); break;
                case 'B': _sb.MoveBy(Get(ps, 0, 1), 0); break;
                case 'C': _sb.MoveBy(0, Get(ps, 0, 1)); break;
                case 'D': _sb.MoveBy(0, -Get(ps, 0, 1)); break;
                case 'J': _sb.EraseInDisplay(Get(ps, 0, 0)); break;
                case 'K': _sb.EraseInLine(Get(ps, 0, 0)); break;
                case 'm': ApplySgr(ps); break;
                case 'h': if (priv) SetMode(ps, true); break;
                case 'l': if (priv) SetMode(ps, false); break;
                // 'r' (scroll region), 'S'/'T' (scroll) intentionally not modeled in v1.
            }
        }

        private void SetMode(List<int> ps, bool on)
        {
            foreach (var p in ps)
            {
                if (p == 1049 || p == 47 || p == 1047) _sb.SetAltScreen(on);
                else if (p == 25) _sb.CursorVisible = on;
                else if (p == 1000 || p == 1002 || p == 1003) _sb.MouseReporting = on; // app owns the mouse
                else if (p == 1006) _sb.MouseSgr = on;                                  // SGR mouse encoding
                // 2004 bracketed paste: handled at the input layer; nothing to render.
            }
        }

        private void ApplySgr(List<int> ps)
        {
            if (ps.Count == 0) { _sb.ResetPen(); return; }
            for (int i = 0; i < ps.Count; i++)
            {
                int p = ps[i];
                if (p == 0) _sb.ResetPen();
                else if (p == 1) _sb.PenFlags |= 1;
                else if (p == 4) _sb.PenFlags |= 2;
                else if (p == 7) _sb.PenFlags |= 4;
                else if (p == 22) _sb.PenFlags &= unchecked((byte)~1);
                else if (p == 24) _sb.PenFlags &= unchecked((byte)~2);
                else if (p == 27) _sb.PenFlags &= unchecked((byte)~4);
                else if (p >= 30 && p <= 37) _sb.PenFg = Palette[p - 30];
                else if (p >= 90 && p <= 97) _sb.PenFg = Palette[8 + (p - 90)];
                else if (p >= 40 && p <= 47) _sb.PenBg = Palette[p - 40];
                else if (p >= 100 && p <= 107) _sb.PenBg = Palette[8 + (p - 100)];
                else if (p == 39) _sb.PenFg = new Color32(200, 200, 200, 255);
                else if (p == 49) _sb.PenBg = new Color32(0, 0, 0, 255);
                else if (p == 38 || p == 48)
                {
                    Color32 col; int consumed;
                    if (TryExtendedColor(ps, i, out col, out consumed)) { if (p == 38) _sb.PenFg = col; else _sb.PenBg = col; i += consumed; }
                }
            }
        }

        // 38;5;n  or  38;2;r;g;b
        private bool TryExtendedColor(List<int> ps, int i, out Color32 col, out int consumed)
        {
            col = default; consumed = 0;
            if (i + 1 >= ps.Count) return false;
            int mode = ps[i + 1];
            if (mode == 5 && i + 2 < ps.Count) { col = Xterm256(ps[i + 2]); consumed = 2; return true; }
            if (mode == 2 && i + 4 < ps.Count) { col = new Color32((byte)ps[i + 2], (byte)ps[i + 3], (byte)ps[i + 4], 255); consumed = 4; return true; }
            return false;
        }

        private static Color32 Xterm256(int n)
        {
            if (n < 16) return Palette[n];
            if (n >= 16 && n <= 231)
            {
                n -= 16; int r = n / 36, g = (n / 6) % 6, b = n % 6;
                return new Color32(Step(r), Step(g), Step(b), 255);
            }
            int gray = 8 + (n - 232) * 10;
            return new Color32((byte)gray, (byte)gray, (byte)gray, 255);
        }

        private static byte Step(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);

        private static List<int> ParseParams(string body)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(body)) return list;
            foreach (var part in body.Split(';'))
                list.Add(int.TryParse(part, out var v) ? v : 0);
            return list;
        }

        // For cursor/erase params: a missing or 0 value means the documented default.
        private static int Get(List<int> ps, int idx, int def) => (idx < ps.Count && ps[idx] > 0) ? ps[idx] : def;

        private static Color32[] BuildPalette()
        {
            return new Color32[]
            {
                new Color32(0,0,0,255), new Color32(205,49,49,255), new Color32(13,188,121,255), new Color32(229,229,16,255),
                new Color32(36,114,200,255), new Color32(188,63,188,255), new Color32(17,168,205,255), new Color32(229,229,229,255),
                new Color32(102,102,102,255), new Color32(241,76,76,255), new Color32(35,209,139,255), new Color32(245,245,67,255),
                new Color32(59,142,234,255), new Color32(214,112,214,255), new Color32(41,184,219,255), new Color32(255,255,255,255),
            };
        }
    }
}
