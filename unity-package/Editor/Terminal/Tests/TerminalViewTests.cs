using NUnit.Framework;
using UnityEngine;
using AgenLink.Terminal;

public class TerminalViewTests
{
    // Regression guard for the "whole terminal blinking" bug. OnGUI runs for several IMGUI
    // event passes per frame; during the Layout pass GUILayoutUtility.GetRect yields a 0x0 rect.
    // Measuring/resizing/painting from that oscillated the terminal size between 1x1 (Layout) and
    // the real size (Repaint) every frame, which blanked the ScreenBuffer and flooded the pty with
    // resizes. The view must only measure/resize/paint on the Repaint pass with a real rect.
    [Test]
    public void ShouldRender_TrueOnlyForRepaintWithRealRect()
    {
        Assert.IsFalse(TerminalView.ShouldRender(EventType.Layout, 0f, 0f), "Layout pass (0x0) must be skipped");
        Assert.IsFalse(TerminalView.ShouldRender(EventType.Layout, 1200f, 800f), "Layout pass must be skipped even if a rect leaks in");
        Assert.IsFalse(TerminalView.ShouldRender(EventType.Repaint, 0f, 0f), "Collapsed/degenerate Repaint rect must be skipped");
        Assert.IsTrue(TerminalView.ShouldRender(EventType.Repaint, 1200f, 800f), "Real Repaint rect must render");
    }

    // Shift+Tab must emit the back-tab CSI Z, not a plain tab — that's what the Claude CLI reads to
    // cycle plan / auto-accept mode. A plain Tab stays a tab.
    [Test]
    public void MapKey_Tab_VsShiftTab()
    {
        var tab = new Event { type = EventType.KeyDown, keyCode = KeyCode.Tab };
        Assert.AreEqual("\t", TerminalView.MapKey(tab), "plain Tab");

        var shiftTab = new Event { type = EventType.KeyDown, keyCode = KeyCode.Tab, modifiers = EventModifiers.Shift };
        Assert.AreEqual("\x1b[Z", TerminalView.MapKey(shiftTab), "Shift+Tab = back-tab");
    }

    [Test]
    public void MapKey_CtrlLetter_EmitsControlByte_ButNotWithShift()
    {
        var ctrlC = new Event { type = EventType.KeyDown, keyCode = KeyCode.C, modifiers = EventModifiers.Control };
        Assert.AreEqual("\x03", TerminalView.MapKey(ctrlC), "Ctrl+C = interrupt byte 0x03");

        // Ctrl+Shift+C is the copy shortcut (handled before MapKey) and must NOT map to an interrupt.
        var ctrlShiftC = new Event { type = EventType.KeyDown, keyCode = KeyCode.C, modifiers = EventModifiers.Control | EventModifiers.Shift };
        Assert.IsNull(TerminalView.MapKey(ctrlShiftC), "Ctrl+Shift+C must not become an interrupt byte");
    }

    // Alt+letter must forward an ESC-prefixed sequence ("meta sends escape"), exactly as Windows
    // Terminal / cmd / PowerShell do. Alt+V (ESC v) is the keystroke the Claude CLI listens for to
    // read a clipboard image — without this the child never gets its image-paste trigger, which is
    // why pasting screenshots did nothing in the embedded terminal.
    [Test]
    public void MapKey_AltLetter_EmitsEscPrefixed()
    {
        var altV = new Event { type = EventType.KeyDown, keyCode = KeyCode.V, modifiers = EventModifiers.Alt };
        Assert.AreEqual("\x1bv", TerminalView.MapKey(altV), "Alt+V = ESC v (Claude clipboard image-paste trigger)");

        var altShiftV = new Event { type = EventType.KeyDown, keyCode = KeyCode.V, modifiers = EventModifiers.Alt | EventModifiers.Shift };
        Assert.AreEqual("\x1bV", TerminalView.MapKey(altShiftV), "Alt+Shift+V = ESC V (uppercase)");

        var altB = new Event { type = EventType.KeyDown, keyCode = KeyCode.B, modifiers = EventModifiers.Alt };
        Assert.AreEqual("\x1bb", TerminalView.MapKey(altB), "Alt+B = ESC b (readline back-word)");

        // AltGr (Ctrl+Alt) must NOT be hijacked into a meta sequence — it composes characters, so it
        // falls through to the printable branch and yields the composed character ('v'), not ESC v.
        var altGrV = new Event { type = EventType.KeyDown, keyCode = KeyCode.V, character = 'v', modifiers = EventModifiers.Alt | EventModifiers.Control };
        Assert.AreEqual("v", TerminalView.MapKey(altGrV), "AltGr (Ctrl+Alt) must compose its character, not emit a meta sequence");
    }
}
