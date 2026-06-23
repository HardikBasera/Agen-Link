using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Terminal
{
    /// <summary>IMGUI surface that paints a ScreenBuffer (with scrollback) and forwards keystrokes to a
    /// TerminalClient. Mouse wheel scrolls the scrollback; click-drag selects text; Ctrl+Shift+C or
    /// right-click copies; Ctrl+V pastes.</summary>
    internal sealed class TerminalView
    {
        private readonly ScreenBuffer _sb;
        private readonly TerminalClient _client;
        private GUIStyle _cell;
        private float _charW, _lineH;
        private int _lastCols = -1, _lastRows = -1;
        public Action<int, int> OnResizeRequest; // cols, rows
        public Action OnRepaintRequest;          // ask the host window to repaint from event handling

        // Scrollback view: lines scrolled up from the live bottom. 0 = following live output.
        private int _scrollLines;
        private int _lastScrollback;
        private const int WheelLines = 3;

        // Drag selection, in absolute-line space (scrollback ++ live screen). See ScreenBuffer.CellAtAbsolute.
        private bool _hasSelection, _selecting;
        private int _selStartLine, _selStartCol, _selEndLine, _selEndCol;

        private static readonly Color SelColor = new Color(0.20f, 0.45f, 0.85f, 0.45f);

        public TerminalView(ScreenBuffer sb, TerminalClient client) { _sb = sb; _client = client; }

        private void EnsureStyle()
        {
            if (_cell != null) return;
            _cell = new GUIStyle(EditorStyles.label)
            {
                font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Menlo", "monospace" }, BridgeSettings.TerminalFontSize),
                fontSize = BridgeSettings.TerminalFontSize,
                richText = false, wordWrap = false,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0),
            };
            var sz = _cell.CalcSize(new GUIContent("M"));
            // _charW is the cell stride: the cursor block and every run's start x are positioned on the
            // grid (col * _charW), but row text is drawn as a STRING via GUI.Label, which advances each
            // glyph by the font's real per-character width. CalcSize("M") includes fixed horizontal
            // padding that does NOT accumulate per glyph inside a drawn string, so using it as the stride
            // overestimates the advance — the grid drifts right of the tightly-packed text and the cursor
            // sits in a gap that grows with line length. Derive the true advance from the slope between
            // two string lengths so the fixed padding cancels out.
            const int N = 64;
            float wideW = _cell.CalcSize(new GUIContent(new string('M', N))).x;
            _charW = (wideW - sz.x) / (N - 1);
            _lineH = _cell.lineHeight > 0 ? _cell.lineHeight : sz.y;
        }

        // OnGUI runs for several IMGUI event passes per frame. During the Layout pass,
        // GUILayoutUtility.GetRect returns a 0x0 rect — measuring/resizing/painting from it
        // oscillates the terminal size between 1x1 and the real size every frame, which blanks
        // the buffer and floods the pty with resizes (the "whole terminal blinking" bug).
        // Only the Repaint pass carries a real rect, so gate all of that work behind it.
        internal static bool ShouldRender(EventType evt, float width, float height)
            => evt == EventType.Repaint && width > 1f && height > 1f;

        public void OnGUI(Rect area)
        {
            EnsureStyle();
            HandleEvents(area);
            if (!ShouldRender(Event.current.type, area.width, area.height)) return;

            int cols = Mathf.Max(1, Mathf.FloorToInt(area.width / Mathf.Max(1f, _charW)));
            int rows = Mathf.Max(1, Mathf.FloorToInt(area.height / Mathf.Max(1f, _lineH)));
            if (cols != _lastCols || rows != _lastRows)
            {
                _lastCols = cols; _lastRows = rows;
                _sb.Resize(cols, rows);
                OnResizeRequest?.Invoke(cols, rows);
            }

            int S = _sb.ScrollbackCount;
            // Keep the viewport anchored to the same lines while scrolled up as new output streams in.
            if (_scrollLines > 0 && S > _lastScrollback) _scrollLines += S - _lastScrollback;
            _lastScrollback = S;
            _scrollLines = Mathf.Clamp(_scrollLines, 0, S);
            int topAbs = S - _scrollLines;   // absolute line shown at screen row 0

            EditorGUI.DrawRect(area, new Color(0.06f, 0.06f, 0.07f, 1f));

            // Normalized selection bounds for this paint.
            bool sel = _hasSelection;
            int nsL = _selStartLine, nsC = _selStartCol, neL = _selEndLine, neC = _selEndCol;
            if (sel && (neL < nsL || (neL == nsL && neC < nsC)))
            { int tl = nsL, tc = nsC; nsL = neL; nsC = neC; neL = tl; neC = tc; }

            for (int y = 0; y < _sb.Rows; y++)
                RenderRow(area, y, topAbs + y, sel, nsL, nsC, neL, neC);

            if (_scrollLines == 0 && _sb.CursorVisible)
            {
                var cr = new Rect(area.x + _sb.CursorCol * _charW, area.y + _sb.CursorRow * _lineH, _charW, _lineH);
                EditorGUI.DrawRect(cr, new Color(0.8f, 0.8f, 0.8f, 0.5f));
            }

            if (_scrollLines > 0) DrawScrollIndicator(area);
        }

        private void RenderRow(Rect area, int y, int absLine, bool sel, int nsL, int nsC, int neL, int neC)
        {
            float rowY = area.y + y * _lineH;
            int cols = _sb.Cols;

            // Pass 1: background fills per styled run.
            int c = 0;
            while (c < cols)
            {
                var first = _sb.CellAtAbsolute(absLine, c);
                int start = c;
                while (c < cols && SameStyle(_sb.CellAtAbsolute(absLine, c), first)) c++;
                if (!IsBlackBg(first.Bg))
                    EditorGUI.DrawRect(new Rect(area.x + start * _charW, rowY, (c - start) * _charW + 1, _lineH), (Color)first.Bg);
            }

            // Pass 2: selection highlight (one contiguous span per row, in reading order).
            if (sel && absLine >= nsL && absLine <= neL)
            {
                int a = absLine == nsL ? nsC : 0;
                int b = absLine == neL ? neC : cols - 1;
                a = Mathf.Clamp(a, 0, cols - 1); b = Mathf.Clamp(b, 0, cols - 1);
                if (b >= a)
                    EditorGUI.DrawRect(new Rect(area.x + a * _charW, rowY, (b - a + 1) * _charW, _lineH), SelColor);
            }

            // Pass 3: text on top, per styled run.
            c = 0;
            while (c < cols)
            {
                var first = _sb.CellAtAbsolute(absLine, c);
                int start = c;
                var txt = new StringBuilder();
                while (c < cols)
                {
                    var cell = _sb.CellAtAbsolute(absLine, c);
                    if (!SameStyle(cell, first)) break;
                    txt.Append(cell.Rune == '\0' ? ' ' : cell.Rune);
                    c++;
                }
                var rect = new Rect(area.x + start * _charW, rowY, (c - start) * _charW + 1, _lineH);
                var prev = _cell.normal.textColor;
                _cell.normal.textColor = (Color)first.Fg;
                GUI.Label(rect, txt.ToString(), _cell);
                _cell.normal.textColor = prev;
            }
        }

        private void DrawScrollIndicator(Rect area)
        {
            string msg = $"▲ {_scrollLines} line{(_scrollLines == 1 ? "" : "s")} up — scroll down or type to return";
            var size = EditorStyles.miniLabel.CalcSize(new GUIContent(msg));
            var bg = new Rect(area.xMax - size.x - 14, area.y + 4, size.x + 8, size.y + 2);
            EditorGUI.DrawRect(bg, new Color(0f, 0f, 0f, 0.65f));
            var prev = GUI.color;
            GUI.color = new Color(0.85f, 0.86f, 0.92f, 1f);
            GUI.Label(new Rect(bg.x + 4, bg.y, size.x, size.y + 2), msg, EditorStyles.miniLabel);
            GUI.color = prev;
        }

        private static bool SameStyle(Cell a, Cell b) =>
            a.Fg.r == b.Fg.r && a.Fg.g == b.Fg.g && a.Fg.b == b.Fg.b &&
            a.Bg.r == b.Bg.r && a.Bg.g == b.Bg.g && a.Bg.b == b.Bg.b;

        private static bool IsBlackBg(Color32 c) => c.r == 0 && c.g == 0 && c.b == 0;

        // ===================== Input =====================

        private void HandleEvents(Rect area)
        {
            var e = Event.current;
            if (e == null) return;
            switch (e.type)
            {
                case EventType.KeyDown:     HandleKey(e); break;
                case EventType.ScrollWheel: HandleWheel(e, area); break;
                case EventType.MouseDown:   HandleMouseDown(e, area); break;
                case EventType.MouseDrag:   HandleMouseDrag(e, area); break;
                case EventType.MouseUp:     HandleMouseUp(e, area); break;
                case EventType.ContextClick: ShowContextMenu(area, e); break;
            }
        }

        private void HandleKey(Event e)
        {
            // Copy selection: Ctrl/Cmd+Shift+C (Ctrl+C alone is the interrupt byte, forwarded below).
            if ((e.control || e.command) && e.shift && e.keyCode == KeyCode.C) { CopySelection(); e.Use(); return; }

            // Paste: Ctrl/Cmd+V (and Ctrl+Shift+V).
            if ((e.control || e.command) && e.keyCode == KeyCode.V) { Paste(); e.Use(); return; }

            // Shift+PageUp/PageDown scroll the scrollback by a page (plain PageUp/Down go to the app).
            if (e.shift && e.keyCode == KeyCode.PageUp)   { ScrollBy(_sb.Rows); e.Use(); return; }
            if (e.shift && e.keyCode == KeyCode.PageDown) { ScrollBy(-_sb.Rows); e.Use(); return; }

            string seq = MapKey(e);
            if (seq != null) { SnapToBottom(); Send(seq); e.Use(); }
        }

        internal static string MapKey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return: case KeyCode.KeypadEnter: return "\r";
                case KeyCode.Backspace: return "\x7f";
                case KeyCode.Tab: return e.shift ? "\x1b[Z" : "\t";   // Shift+Tab = back-tab (CSI Z)
                case KeyCode.Escape: return "\x1b";
                case KeyCode.UpArrow: return "\x1b[A";
                case KeyCode.DownArrow: return "\x1b[B";
                case KeyCode.RightArrow: return "\x1b[C";
                case KeyCode.LeftArrow: return "\x1b[D";
                case KeyCode.Home: return "\x1b[H";
                case KeyCode.End: return "\x1b[F";
                case KeyCode.PageUp: return "\x1b[5~";
                case KeyCode.PageDown: return "\x1b[6~";
                case KeyCode.Delete: return "\x1b[3~";
            }
            // Alt/Meta+letter -> ESC-prefixed byte ("meta sends escape", as xterm/cmd/PowerShell do).
            // Alt+V (ESC v) is the keystroke the Claude CLI listens for to read a clipboard image itself;
            // Alt+B / Alt+F / Alt+D drive readline word navigation. We forward the keystroke and let the
            // child read the OS clipboard, exactly like a real terminal. Excludes Ctrl so AltGr (Ctrl+Alt)
            // still composes its character via the printable branch below.
            if (e.alt && !e.control && e.keyCode >= KeyCode.A && e.keyCode <= KeyCode.Z)
            {
                char ch = (char)(e.keyCode - KeyCode.A + 'a');
                return "\x1b" + (e.shift ? char.ToUpperInvariant(ch) : ch);
            }
            // Ctrl+letter -> control byte (Ctrl+A..Ctrl+Z). Excludes Shift/Alt so combos like
            // Ctrl+Shift+C (copy) are not hijacked into an interrupt.
            if (e.control && !e.shift && !e.alt && e.keyCode >= KeyCode.A && e.keyCode <= KeyCode.Z)
                return ((char)(e.keyCode - KeyCode.A + 1)).ToString();
            // Printable
            if (e.character != '\0' && !char.IsControl(e.character))
                return e.character.ToString();
            return null;
        }

        private void HandleWheel(Event e, Rect area)
        {
            if (!area.Contains(e.mousePosition)) return;

            // A full-screen app (Claude on the alt screen) that requested mouse tracking owns the wheel —
            // forward it as SGR mouse events so the APP scrolls its own conversation, exactly like a real
            // terminal. Our local scrollback is only for plain output (the shell prompt) where no app
            // grabs the mouse; forwarding here also means we never enter local-scroll on the alt screen,
            // so there's no scrollback/live-screen mixing to look like "repeats".
            if (_sb.MouseReporting && _sb.MouseSgr)
            {
                int row = Mathf.Clamp(Mathf.FloorToInt((e.mousePosition.y - area.y) / Mathf.Max(1f, _lineH)), 0, _sb.Rows - 1) + 1;
                int col = Mathf.Clamp(Mathf.FloorToInt((e.mousePosition.x - area.x) / Mathf.Max(1f, _charW)), 0, _sb.Cols - 1) + 1;
                int btn = e.delta.y > 0 ? 65 : 64;   // 64 = wheel up, 65 = wheel down
                int notches = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(e.delta.y)), 1, 4);
                var seq = new StringBuilder();
                for (int i = 0; i < notches; i++)
                    seq.Append("\x1b[<").Append(btn).Append(';').Append(col).Append(';').Append(row).Append('M');
                Send(seq.ToString());
                e.Use();
                return;
            }

            int step = e.delta.y > 0 ? -WheelLines : WheelLines;   // wheel up = into local scrollback
            _scrollLines = Mathf.Clamp(_scrollLines + step, 0, _sb.ScrollbackCount);
            e.Use(); RequestRepaint();
        }

        private void HandleMouseDown(Event e, Rect area)
        {
            if (e.button != 0 || !area.Contains(e.mousePosition)) return;
            GUIUtility.keyboardControl = 0; // ensure no other IMGUI control swallows our keys
            PointToCell(e.mousePosition, area, out int line, out int col);
            _selStartLine = _selEndLine = line; _selStartCol = _selEndCol = col;
            _selecting = true; _hasSelection = false;
            e.Use(); RequestRepaint();
        }

        private void HandleMouseDrag(Event e, Rect area)
        {
            if (e.button != 0 || !_selecting) return;
            PointToCell(e.mousePosition, area, out int line, out int col);
            _selEndLine = line; _selEndCol = col;
            _hasSelection = !(_selStartLine == _selEndLine && _selStartCol == _selEndCol);
            e.Use(); RequestRepaint();
        }

        private void HandleMouseUp(Event e, Rect area)
        {
            if (e.button != 0 || !_selecting) return;
            _selecting = false;
            PointToCell(e.mousePosition, area, out int line, out int col);
            _selEndLine = line; _selEndCol = col;
            _hasSelection = !(_selStartLine == _selEndLine && _selStartCol == _selEndCol);
            e.Use(); RequestRepaint();
        }

        private void ShowContextMenu(Rect area, Event e)
        {
            if (!area.Contains(e.mousePosition)) return;
            var menu = new GenericMenu();
            if (_hasSelection) menu.AddItem(new GUIContent("Copy"), false, CopySelection);
            else menu.AddDisabledItem(new GUIContent("Copy"));
            menu.AddItem(new GUIContent("Copy all"), false, CopyAll);
            menu.AddItem(new GUIContent("Select all"), false, SelectAll);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Paste"), false, Paste);
            menu.ShowAsContext();
            e.Use();
        }

        private void PointToCell(Vector2 p, Rect area, out int absLine, out int col)
        {
            int yRow = Mathf.Clamp(Mathf.FloorToInt((p.y - area.y) / Mathf.Max(1f, _lineH)), 0, _sb.Rows - 1);
            col = Mathf.Clamp(Mathf.FloorToInt((p.x - area.x) / Mathf.Max(1f, _charW)), 0, _sb.Cols - 1);
            int S = _sb.ScrollbackCount;
            absLine = Mathf.Clamp((S - _scrollLines) + yRow, 0, S + _sb.Rows - 1);
        }

        private void CopySelection()
        {
            if (!_hasSelection) return;
            string t = _sb.SelectionText(_selStartLine, _selStartCol, _selEndLine, _selEndCol);
            if (!string.IsNullOrEmpty(t)) EditorGUIUtility.systemCopyBuffer = t;
        }

        private void CopyAll()
        {
            int S = _sb.ScrollbackCount;
            EditorGUIUtility.systemCopyBuffer = _sb.SelectionText(0, 0, S + _sb.Rows - 1, _sb.Cols - 1);
        }

        private void SelectAll()
        {
            _selStartLine = 0; _selStartCol = 0;
            _selEndLine = _sb.ScrollbackCount + _sb.Rows - 1; _selEndCol = _sb.Cols - 1;
            _hasSelection = true; RequestRepaint();
        }

        private void Paste()
        {
            string clip = EditorGUIUtility.systemCopyBuffer ?? "";
            SnapToBottom();
            Send("\x1b[200~" + clip + "\x1b[201~"); // bracketed paste
        }

        private void ScrollBy(int lines)
        {
            _scrollLines = Mathf.Clamp(_scrollLines + lines, 0, _sb.ScrollbackCount);
            RequestRepaint();
        }

        private void SnapToBottom()
        {
            if (_scrollLines != 0) { _scrollLines = 0; RequestRepaint(); }
        }

        private void RequestRepaint() => OnRepaintRequest?.Invoke();

        private void Send(string s) => _client.SendInput(Encoding.UTF8.GetBytes(s));
    }
}
