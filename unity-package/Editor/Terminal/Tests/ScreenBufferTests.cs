using NUnit.Framework;
using AgenLink.Terminal;

public class ScreenBufferTests
{
    [Test]
    public void Put_AdvancesCursor_AndStoresRune()
    {
        var sb = new ScreenBuffer(10, 3);
        sb.Put('A'); sb.Put('B');
        Assert.AreEqual('A', sb.CellAt(0, 0).Rune);
        Assert.AreEqual('B', sb.CellAt(0, 1).Rune);
        Assert.AreEqual(2, sb.CursorCol);
    }

    [Test]
    public void Newline_And_CarriageReturn_MoveCursor()
    {
        var sb = new ScreenBuffer(10, 3);
        sb.Put('X'); sb.LineFeed(); sb.CarriageReturn();
        Assert.AreEqual(1, sb.CursorRow);
        Assert.AreEqual(0, sb.CursorCol);
    }

    [Test]
    public void Wrap_AtRightMargin_GoesToNextRow()
    {
        var sb = new ScreenBuffer(2, 3);
        sb.Put('a'); sb.Put('b'); sb.Put('c');
        Assert.AreEqual('c', sb.CellAt(1, 0).Rune);
    }

    [Test]
    public void ScrollUp_WhenLineFeedPastBottom()
    {
        var sb = new ScreenBuffer(3, 2);
        sb.Put('1'); sb.LineFeed(); sb.CarriageReturn();
        sb.Put('2'); sb.LineFeed(); sb.CarriageReturn(); // forces a scroll
        sb.Put('3');
        Assert.AreEqual('2', sb.CellAt(0, 0).Rune);
        Assert.AreEqual('3', sb.CellAt(1, 0).Rune);
    }

    [Test]
    public void AltScreen_IsIsolatedFromPrimary()
    {
        var sb = new ScreenBuffer(4, 2);
        sb.Put('P');
        sb.SetAltScreen(true);
        Assert.AreEqual('\0', sb.CellAt(0, 0).Rune);
        sb.SetAltScreen(false);
        Assert.AreEqual('P', sb.CellAt(0, 0).Rune);
    }

    // Build a 1-col-per-row helper: write text on the current row.
    private static void WriteRow(ScreenBuffer sb, string text)
    {
        foreach (char ch in text) sb.Put(ch);
    }

    [Test]
    public void ScrollUp_RetainsEvictedLine_InScrollback()
    {
        var sb = new ScreenBuffer(3, 2);
        WriteRow(sb, "1"); sb.LineFeed(); sb.CarriageReturn();
        WriteRow(sb, "2"); sb.LineFeed(); sb.CarriageReturn(); // scrolls "1" off the top
        Assert.AreEqual(1, sb.ScrollbackCount, "evicted line must be retained");
        Assert.AreEqual('1', sb.CellAtAbsolute(0, 0).Rune, "scrollback line 0 is the evicted '1'");
    }

    [Test]
    public void AltScreen_DoesNotAccumulateScrollback()
    {
        var sb = new ScreenBuffer(3, 2);
        sb.SetAltScreen(true);
        WriteRow(sb, "a"); sb.LineFeed(); sb.CarriageReturn();
        WriteRow(sb, "b"); sb.LineFeed(); sb.CarriageReturn();
        WriteRow(sb, "c"); sb.LineFeed(); sb.CarriageReturn();
        Assert.AreEqual(0, sb.ScrollbackCount, "alt screen must never add scrollback");
    }

    [Test]
    public void CellAtAbsolute_MapsScrollbackThenLiveScreen()
    {
        var sb = new ScreenBuffer(3, 2);
        WriteRow(sb, "1"); sb.LineFeed(); sb.CarriageReturn();
        WriteRow(sb, "2"); sb.LineFeed(); sb.CarriageReturn(); // "1" -> scrollback; screen: "2",""
        WriteRow(sb, "3");
        Assert.AreEqual('1', sb.CellAtAbsolute(0, 0).Rune, "abs 0 = scrollback");
        Assert.AreEqual('2', sb.CellAtAbsolute(sb.ScrollbackCount + 0, 0).Rune, "abs = live row 0");
        Assert.AreEqual('3', sb.CellAtAbsolute(sb.ScrollbackCount + 1, 0).Rune, "abs = live row 1");
    }

    [Test]
    public void SelectionText_SpansScrollback_AndRightTrims()
    {
        var sb = new ScreenBuffer(5, 2);
        WriteRow(sb, "ab"); sb.LineFeed(); sb.CarriageReturn();   // "ab" -> scrollback after next scroll
        WriteRow(sb, "cd"); sb.LineFeed(); sb.CarriageReturn();   // "ab" evicted; screen row0="cd"
        WriteRow(sb, "ef");
        // Absolute lines: 0="ab"(scrollback), 1="cd"(row0), 2="ef"(row1)
        string text = sb.SelectionText(0, 0, 2, 4);
        Assert.AreEqual("ab\ncd\nef", text, "spans scrollback+screen, trailing blanks trimmed");
    }

    [Test]
    public void SelectionText_NormalizesReversedRange()
    {
        var sb = new ScreenBuffer(5, 2);
        WriteRow(sb, "hi");
        Assert.AreEqual("hi", sb.SelectionText(sb.ScrollbackCount, 1, sb.ScrollbackCount, 0));
    }

    [Test]
    public void Scrollback_SkipsLeadingBlankLines()
    {
        var sb = new ScreenBuffer(3, 2);
        for (int i = 0; i < 5; i++) { sb.LineFeed(); sb.CarriageReturn(); } // blank scrolls at startup
        Assert.AreEqual(0, sb.ScrollbackCount, "leading blank lines must not pollute scrollback");

        WriteRow(sb, "X"); sb.LineFeed(); sb.CarriageReturn();
        WriteRow(sb, "Y"); sb.LineFeed(); sb.CarriageReturn(); // scroll evicts real content "X"
        Assert.AreEqual('X', sb.CellAtAbsolute(0, 0).Rune, "real content is still captured");
    }

    [Test]
    public void Scrollback_CollapsesConsecutiveBlankLinesToOne()
    {
        var sb = new ScreenBuffer(3, 2);
        WriteRow(sb, "A"); sb.LineFeed(); sb.CarriageReturn();
        sb.LineFeed(); sb.CarriageReturn();   // evict "A" -> scrollback ["A"]
        for (int i = 0; i < 4; i++) { sb.LineFeed(); sb.CarriageReturn(); } // 4 blank scrolls
        // "A" plus a single separator blank — never a run of blanks.
        Assert.AreEqual(2, sb.ScrollbackCount, "consecutive blanks collapse to one separator");
        Assert.AreEqual('A', sb.CellAtAbsolute(0, 0).Rune);
        Assert.AreEqual('\0', sb.CellAtAbsolute(1, 0).Rune, "single trailing blank kept");
    }
}
