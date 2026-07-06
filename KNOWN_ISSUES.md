# Known issues

Bugs and limitations we're aware of and have chosen to defer. If you hit one, you're not seeing
something new — and fixes are welcome (see [CONTRIBUTING.md](CONTRIBUTING.md)).

Last reviewed: 2026-07-06 (v0.2.1).

## Terminal

### Maximizing the Agen-Link window drops the terminal session

Deliberately maximizing and then restoring the Agen-Link Editor window — double-clicking the window
tab, or Unity's "Maximize" command — recreates the window, and the embedded terminal loses its
connection to the running CLI session.

- **Severity:** low.
- **Note:** the *accidental* trigger is gone — pressing <kbd>Shift</kbd>+<kbd>Space</kbd> while typing
  no longer maximizes the window, because the Terminal tab now captures the keyboard while focused. So
  this only happens on an intentional maximize.
- **Workaround:** avoid maximizing the window mid-session; if you do, press **⟳ Restart** to start a
  fresh session. The underlying CLI process keeps running, but scrollback from before the maximize is
  not restored to the new view.

### Mouse clicks and drags are not forwarded to the CLI

The terminal forwards the scroll wheel, text selection/copy, paste, and the keyboard (including
<kbd>Shift</kbd>+<kbd>Tab</kbd>), but mouse **clicks and drags** are not yet forwarded to the child
CLI. Full-screen terminal apps that rely on mouse positioning won't receive those events.

- **Severity:** low.
- **Workaround:** use keyboard navigation.

---

Something not listed here? Please open an issue:
<https://github.com/HardikBasera/Agen-Link/issues>.
