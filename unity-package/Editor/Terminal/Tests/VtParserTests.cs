using NUnit.Framework;
using System.Text;
using AgenLink.Terminal;

public class VtParserTests
{
    private static ScreenBuffer Run(string s, int cols = 20, int rows = 5)
    {
        var sb = new ScreenBuffer(cols, rows);
        var p = new VtParser(sb);
        p.Feed(Encoding.UTF8.GetBytes(s));
        return sb;
    }

    [Test]
    public void Plain_Text_Writes()
    {
        var sb = Run("hi");
        Assert.AreEqual('h', sb.CellAt(0, 0).Rune);
        Assert.AreEqual('i', sb.CellAt(0, 1).Rune);
    }

    [Test]
    public void CUP_MovesCursor()
    {
        var sb = Run("\x1b[2;3HX"); // row 2 col 3 (1-based) -> X at (1,2)
        Assert.AreEqual('X', sb.CellAt(1, 2).Rune);
    }

    [Test]
    public void SGR_Red_Foreground_SetsPen()
    {
        var sb = Run("\x1b[31mR");
        var c = sb.CellAt(0, 0);
        Assert.Greater(c.Fg.r, c.Fg.g);
        Assert.Greater(c.Fg.r, c.Fg.b);
    }

    [Test]
    public void EL_ClearsToEndOfLine()
    {
        var sb = Run("abc\x1b[1;1H\x1b[K"); // write abc, home, erase line
        Assert.AreEqual('\0', sb.CellAt(0, 0).Rune);
    }

    [Test]
    public void AltScreen_Enter_Hides_Primary()
    {
        var sb = Run("P\x1b[?1049h"); // 'P' on primary, then switch to alt
        Assert.AreEqual('\0', sb.CellAt(0, 0).Rune);
    }

    [Test]
    public void OSC_Title_IsIgnored_NoCrash()
    {
        var sb = Run("\x1b]0;my title\x07Z");
        Assert.AreEqual('Z', sb.CellAt(0, 0).Rune);
    }

    [Test]
    public void MouseTracking_Modes_AreTracked()
    {
        // Claude enables mouse tracking (1000/1002/1003) + SGR encoding (1006) on the alt screen.
        var sb = Run("\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h");
        Assert.IsTrue(sb.MouseReporting, "any of 1000/1002/1003 -> mouse reporting on");
        Assert.IsTrue(sb.MouseSgr, "1006 -> SGR mouse encoding on");

        var off = Run("\x1b[?1000h\x1b[?1006h\x1b[?1003l\x1b[?1006l");
        Assert.IsFalse(off.MouseSgr, "1006l turns SGR off");
    }
}
