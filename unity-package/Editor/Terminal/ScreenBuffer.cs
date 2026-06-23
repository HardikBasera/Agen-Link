using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AgenLink.Terminal
{
    internal struct Cell
    {
        public char Rune;
        public Color32 Fg;
        public Color32 Bg;
        public byte Flags; // 1=bold 2=underline 4=inverse
    }

    /// <summary>A terminal screen: primary + alternate grids, cursor, current SGR pen, scroll.</summary>
    internal sealed class ScreenBuffer
    {
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        public int CursorCol { get; private set; }
        public int CursorRow { get; private set; }
        public bool CursorVisible = true;

        // Terminal modes the running app requested. When MouseReporting is on (a full-screen app like
        // Claude on the alt screen), the wheel/clicks belong to the app, not our local scrollback.
        public bool MouseReporting;   // DECSET 1000/1002/1003 (mouse tracking)
        public bool MouseSgr;         // DECSET 1006 (SGR mouse encoding)

        public Color32 PenFg = new Color32(200, 200, 200, 255);
        public Color32 PenBg = new Color32(0, 0, 0, 255);
        public byte PenFlags;

        private Cell[] _primary;
        private Cell[] _alt;
        private Cell[] _active;
        private bool _onAlt;

        // Lines that scroll off the top of the PRIMARY screen are retained here (oldest first) so the
        // view can scroll back through history. The alt screen (full-screen TUIs) intentionally has no
        // scrollback. Each entry is a row snapshot at its capture-time width.
        private readonly List<Cell[]> _scrollback = new List<Cell[]>();
        private const int MaxScrollback = 5000;

        /// <summary>Number of retained scrollback lines above the live screen.</summary>
        public int ScrollbackCount => _scrollback.Count;

        public ScreenBuffer(int cols, int rows) { Resize(cols, rows); }

        public void Resize(int cols, int rows)
        {
            cols = Mathf.Max(1, cols); rows = Mathf.Max(1, rows);
            var np = New(cols, rows); var na = New(cols, rows);
            if (_primary != null) Copy(_primary, Cols, Rows, np, cols, rows);
            if (_alt != null) Copy(_alt, Cols, Rows, na, cols, rows);
            _primary = np; _alt = na;
            Cols = cols; Rows = rows;
            _active = _onAlt ? _alt : _primary;
            CursorCol = Mathf.Min(CursorCol, cols - 1);
            CursorRow = Mathf.Min(CursorRow, rows - 1);
        }

        public Cell CellAt(int row, int col) => _active[row * Cols + col];

        // Absolute line space spans scrollback then the live screen:
        //   [0, ScrollbackCount)                      -> scrollback (oldest .. newest)
        //   [ScrollbackCount, ScrollbackCount + Rows) -> live screen rows 0 .. Rows-1
        public Cell CellAtAbsolute(int absLine, int col)
        {
            if (col < 0 || col >= Cols || absLine < 0) return Blank();
            if (absLine < _scrollback.Count)
            {
                var row = _scrollback[absLine];
                return col < row.Length ? row[col] : Blank();
            }
            int r = absLine - _scrollback.Count;
            return r >= 0 && r < Rows ? _active[r * Cols + col] : Blank();
        }

        /// <summary>Text of an inclusive cell range in absolute-line space, normalized and right-trimmed
        /// per line. Used for clipboard copy of a drag selection.</summary>
        public string SelectionText(int startLine, int startCol, int endLine, int endCol)
        {
            if (endLine < startLine || (endLine == startLine && endCol < startCol))
            {
                int tl = startLine, tc = startCol;
                startLine = endLine; startCol = endCol; endLine = tl; endCol = tc;
            }
            var outSb = new StringBuilder();
            for (int line = startLine; line <= endLine; line++)
            {
                int c0 = line == startLine ? startCol : 0;
                int c1 = line == endLine ? endCol : Cols - 1;
                c0 = Mathf.Clamp(c0, 0, Cols - 1);
                c1 = Mathf.Clamp(c1, 0, Cols - 1);
                var lineSb = new StringBuilder();
                for (int col = c0; col <= c1; col++)
                {
                    char ch = CellAtAbsolute(line, col).Rune;
                    lineSb.Append(ch == '\0' ? ' ' : ch);
                }
                int len = lineSb.Length;
                while (len > 0 && lineSb[len - 1] == ' ') len--;   // right-trim trailing blanks
                lineSb.Length = len;
                outSb.Append(lineSb);
                if (line != endLine) outSb.Append('\n');
            }
            return outSb.ToString();
        }

        public void Put(char c)
        {
            if (CursorCol >= Cols) { CursorCol = 0; LineFeed(); }
            int i = CursorRow * Cols + CursorCol;
            _active[i] = new Cell { Rune = c, Fg = PenFg, Bg = PenBg, Flags = PenFlags };
            CursorCol++;
        }

        public void CarriageReturn() => CursorCol = 0;

        public void LineFeed()
        {
            if (CursorRow + 1 >= Rows) ScrollUp();
            else CursorRow++;
        }

        public void Backspace() { if (CursorCol > 0) CursorCol--; }

        public void Tab() { int n = 8 - (CursorCol % 8); for (int k = 0; k < n && CursorCol < Cols; k++) CursorCol++; }

        public void MoveTo(int row, int col)
        {
            CursorRow = Mathf.Clamp(row, 0, Rows - 1);
            CursorCol = Mathf.Clamp(col, 0, Cols - 1);
        }

        public void MoveBy(int dRow, int dCol) => MoveTo(CursorRow + dRow, CursorCol + dCol);

        public void EraseInLine(int mode)
        {
            int start = mode == 0 ? CursorCol : 0;
            int end = mode == 1 ? CursorCol + 1 : Cols;
            for (int c = start; c < end; c++) _active[CursorRow * Cols + c] = Blank();
        }

        public void EraseInDisplay(int mode)
        {
            if (mode == 2 || mode == 3) { if (mode == 3) _scrollback.Clear(); for (int i = 0; i < _active.Length; i++) _active[i] = Blank(); return; }
            int from = mode == 0 ? CursorRow * Cols + CursorCol : 0;
            int to = mode == 1 ? CursorRow * Cols + CursorCol + 1 : _active.Length;
            for (int i = from; i < to; i++) _active[i] = Blank();
        }

        public void SetAltScreen(bool on)
        {
            if (on == _onAlt) return;
            _onAlt = on;
            _active = on ? _alt : _primary;
            if (on) { for (int i = 0; i < _alt.Length; i++) _alt[i] = Blank(); MoveTo(0, 0); }
        }

        public void ResetPen() { PenFg = new Color32(200, 200, 200, 255); PenBg = new Color32(0, 0, 0, 255); PenFlags = 0; }

        private void ScrollUp()
        {
            if (!_onAlt)   // alt screen (full-screen apps) never contributes scrollback
            {
                var evicted = new Cell[Cols];
                System.Array.Copy(_active, 0, evicted, 0, Cols);
                // Terminal UIs line-feed runs of empty rows off the top (startup padding, frame
                // redraws). Left in scrollback they show as a big blank gap when you scroll up. Drop a
                // blank line if it would lead the scrollback or directly follow another blank; keep
                // single blank separators between real content.
                bool blank = IsRowBlank(evicted);
                bool prevBlank = _scrollback.Count == 0 || IsRowBlank(_scrollback[_scrollback.Count - 1]);
                if (!(blank && prevBlank))
                {
                    _scrollback.Add(evicted);
                    if (_scrollback.Count > MaxScrollback) _scrollback.RemoveAt(0);
                }
            }
            System.Array.Copy(_active, Cols, _active, 0, (Rows - 1) * Cols);
            for (int c = 0; c < Cols; c++) _active[(Rows - 1) * Cols + c] = Blank();
        }

        private static bool IsRowBlank(Cell[] row)
        {
            for (int i = 0; i < row.Length; i++)
                if (row[i].Rune != '\0' && row[i].Rune != ' ') return false;
            return true;
        }

        private Cell Blank() => new Cell { Rune = '\0', Fg = PenFg, Bg = PenBg, Flags = 0 };
        private static Cell[] New(int cols, int rows) => new Cell[cols * rows];

        private static void Copy(Cell[] src, int sc, int sr, Cell[] dst, int dc, int dr)
        {
            int rows = Mathf.Min(sr, dr), cols = Mathf.Min(sc, dc);
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) dst[r * dc + c] = src[r * sc + c];
        }
    }
}
