# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.2] - 2026-08-24

### Changed
- **`agen_set_component_properties` now sets many objects in one call and reports what it wrote.** Setting
  three transforms on a live scene took **ten** bridge calls: one find, three lookups, three sets, three
  read-backs — and each call is a separate round trip, so the tool surface, not the work, set the pace.
  Three causes, all removed. The tool takes `targets: [..]` instead of one `target` per call. Its reply now
  carries `values`, the post-write value of everything that applied, read back from a fresh
  `SerializedObject` — so it reflects what Unity actually stored (a normalized quaternion, a clamped range),
  which is precisely what a caller would otherwise re-read to learn. And a batch collapses into a **single
  Undo group**, so one call is one Ctrl+Z. The same edit is now two calls instead of ten.
- **`agen_get_gameobject` is no longer advertised as mandatory before every write.** Its description told
  callers to read an object first to discover field names, and `agen_set_component_properties` repeated the
  instruction — so a `Transform` edit paid a discovery read for names that are never in doubt. Both now say
  to look it up only when a component's fields are genuinely unknown (typically a project MonoBehaviour),
  and that confirming a write needs no read at all.
  **Tool descriptions changed — restart the terminal session** or the CLI keeps the old text and will keep
  calling one object at a time.
- **A failed port bind is no longer reported as an error until it actually persists.** The listener rebinds
  on every domain reload, and a rebind that loses the race with the socket from the previous domain
  recovers by itself within seconds. Reporting that as `LogError` painted a self-healing condition as a
  dead bridge — red Console entries that sent us hunting a failure that had already fixed itself. The first
  failures now log a warning that says what they usually mean; only a bind that is still failing after 60s
  escalates to an error, because at that point the port really is taken and tool calls cannot reach the
  Editor. A successful bind that follows failures now logs how long it took to recover, since Unity never
  clears the Console across a domain reload and the earlier entries otherwise sit there looking unresolved.

### Fixed
- **"Could not bind port" no longer strands the bridge until Unity is restarted.** Windows creates socket
  handles **inheritable** by default, so every process the Editor spawned after the bridge bound received a
  duplicate of the listening socket — the pty-host, the CLI it starts, and the MCP server that one starts.
  A duplicate keeps the port bound no matter what the Editor does with its own copy, so `Stop()` closed the
  listener, reported success, and the port stayed `LISTENING` under Unity's PID with nothing able to accept
  on it. Every rebind after the next domain reload then failed forever, and only restarting the Editor
  cleared it — because that kills the terminal's process tree and its inherited copies along with it. This
  is the real cause behind the long-running "the bridge works at first, then stops" reports, and behind the
  earlier conclusion that the port was reserved by Windows: an unrelated process genuinely could not bind
  it either, because Unity's own descendants were holding it. `HANDLE_FLAG_INHERIT` is now cleared on the
  listener as soon as it binds, and on every accepted socket — those share the listener's local port, so an
  inherited copy of one pins the port just as effectively.
- **The accept thread could pin the listening socket open.** It waited in a blocking `AcceptTcpClient`, and
  on Unity's Mono runtime closing a socket does not wake a thread already parked there; `SafeSocketHandle`
  is reference counted, so the real `closesocket()` was deferred until that call returned, which it never
  did. `Stop()`'s one-second join then timed out in silence. The loop now polls with a timeout instead, so
  it exits within one poll interval and the close actually takes effect. `Poll` returns the moment a
  connection is pending, so accept latency is unchanged, and a join that still times out now warns rather
  than being swallowed. Also fixes a latent `NullReferenceException`: the loop read the `_listener` field
  that `Stop()` sets to null.
- **A failed bind leaked a socket handle on every retry.** The `catch` dropped the listener without closing
  it, and setting `ExclusiveAddressUse` just above had already forced the underlying `Socket` into
  existence — so each attempt abandoned a live handle to the GC, on the one path that runs precisely when
  things are already going wrong and that the health watchdog re-runs every three seconds.
- **The escalated bind error asserted a cause it never checked.** It claimed "another Editor most likely
  holds the port", which was wrong on the very case that produced it — a single Editor, holding the port
  itself — and sent us looking for a second Unity that did not exist. It now says how to tell the cases
  apart with `netstat` and what each one means.
- **EditMode test fixtures could be stranded in the user's open scene.** `OpsTests` builds its fixtures with
  `new GameObject()` in whatever scene is open and cleans them up in `[TearDown]` — which only runs if the
  test finishes. A domain reload mid-run, or an aborted run, discards the tracking list and leaves the
  objects behind in real work, with the scene marked dirty; two stray `OpsComp` objects were found that way
  in a live project. A `[SetUp]` sweep now removes leftovers before each test rather than trusting the
  previous run's exit, which also removes a real source of flakiness (the find-by-name and
  ambiguous-sibling tests both count objects that a stray would change). It matches exact fixture names,
  not an `Ops` prefix, so a user object that merely starts with those letters is never touched.
- **Anything appended after a logged exception message was silently dropped.** A Windows socket exception
  arrives as a 256-character buffer padded with NULs (only ~92 of them are real text), and Unity logs
  through a native `char*` that stops at the first NUL — so the sentence explaining what to do about a bind
  failure never reached the Console, and `Trim()` could not help because NUL is not whitespace. Control
  characters are now replaced before the message is composed.
- **A play-mode game capture failed outright.** `agen_capture_screenshot` used
  `ScreenCapture.CaptureScreenshotAsTexture`, which only produces a valid texture at the END of a frame,
  while the bridge dispatches mid-frame from `EditorApplication.update` — so every capture taken in play
  mode died with "Passed in texture is invalid (null)". It now uses `ScreenCapture.CaptureScreenshot` and
  completes the request from a later editor tick, once the PNG is actually on disk. That path is the only
  one that includes screen-space-overlay UI, so rendering a camera instead is not a substitute.
- **Every empty GameObject created through the bridge came back mis-named.** `agen_create_gameobject`
  named the object before uniquifying it, so `GameObjectUtility.GetUniqueNameForSibling` counted the object
  as one of its own siblings and always returned `Name (1)`. Names are now uniquified against siblings
  excluding the object itself. The same self-collision applied to renaming through `agen_modify_gameobject`.
- **A `copyFrom` duplicate kept the name of its source**, leaving two siblings sharing one hierarchy path,
  so path-based targeting silently resolved to whichever happened to come first. Copies are uniquified too.

## [0.3.1] - 2026-08-23

### Fixed
- **`agen_playmode` play/stop and `agen_refresh_assets` no longer destroy the reply they are sending.**
  Each triggers a domain reload, which closes the listener and every client socket — including the one the
  response still had to travel over — so a call that had actually worked was reported to the caller as a
  failure. They now answer first and perform the reload a few frames later. The returned `isPlaying` /
  `isCompiling` are therefore the pre-change values and are documented as such; the tool descriptions tell
  the caller to poll for the new state.
## [0.3.0] - 2026-08-23

### Added
- **`agen_ping`** — a health probe answered on the bridge socket thread without touching Unity, so it
  replies even while the Editor is busy. It reports whether the editor loop is ticking, so a timeout can
  be attributed to the right half instead of guessed at.

### Fixed
- **The bridge could bind a second socket over its own orphaned listener, wedging every request.** The
  listener set `SO_REUSEADDR`, which on Windows permits binding over an *actively listening* socket
  (unlike Unix, where it only covers `TIME_WAIT`) — and which socket then receives connections is
  undefined. If an accept thread outlived a domain reload it kept the port, the next `Start` silently bound
  a second socket and logged "Listening" as normal, and the kernel handed connections to the orphan, where
  stale code answered nothing. The port looked healthy, connects succeeded, every request including
  `agen_ping` timed out, and `CLOSE_WAIT` accumulated. Entering play mode (two reloads back to back) made
  it most likely. The listener now uses `ExclusiveAddressUse`, so a collision is a loud bind failure that
  the new health check retries, instead of a silent wedge.
- **The accept thread can no longer outlive its domain.** `Stop` now closes the underlying socket as well
  as the listener and joins the thread before returning.
- **The bridge self-heals.** A health check on the editor tick asserts, every few seconds, that the listener
  is bound and its accept thread alive, and rebuilds it otherwise — a backstop for failure modes not yet
  identified, rather than another one-shot hook per bug.
- **A stalled Unity main thread no longer leaks a thread and a socket per request.** The bridge waited on
  the main thread with an unbounded `GetResult()`, so any stall (asset import, shader compile, modal
  dialog) blocked the socket thread forever; the connection was never disposed, sat in `CLOSE_WAIT`, and
  every retry leaked another. The wait is now bounded and returns a diagnostic that distinguishes "this
  command is slow" from "Unity's main thread is not ticking".
- **The MCP bridge could silently stop listening after a domain reload.** The listener was restarted
  only from `EditorApplication.delayCall`, which needs an editor tick to fire — and an unfocused
  editor does not tick. A recompile that landed while Unity was in the background left the bridge dead
  until the next reload that happened to occur with the editor focused, with nothing logged and the
  earlier "Listening on ..." line still sitting in the Console. The listener now also restarts from
  `AssemblyReloadEvents.afterAssemblyReload`, which fires as part of the reload itself.

### Documentation
- **Documented that `Add package from git URL` is not supported** and why: the Unity package lives
  in `unity-package/` (no root `package.json`), and the Editor resolves the `mcp-server`/`pty-host`
  helpers as siblings of the package folder, which a git-URL install into `Library/PackageCache`
  cannot provide. Added a Known-issues entry plus README/INSTALL notes; `Add package from disk`
  remains the supported path.

### Changed
- **Minimum supported Node.js is now 20** (was 18). `@modelcontextprotocol/sdk` 1.30 resolves
  `@hono/node-server` to 2.x, which declares `engines: node >=20`. CI has only ever tested on
  Node 20, so 18 was never actually verified.

## [0.2.1] - 2026-07-06

### Fixed
- **Terminal keyboard handling.** <kbd>Ctrl</kbd>+<kbd>Backspace</kbd> deletes a word;
  <kbd>Alt</kbd>+<kbd>Backspace</kbd> and modifier-encoded arrows / Home / End / Delete / PageUp-Down
  (<kbd>Ctrl</kbd>+<kbd>←</kbd>/<kbd>→</kbd> word-jump, etc.) now reach the CLI.
- **Terminal keystrokes no longer trigger Unity editor shortcuts.** Typing in the terminal used to
  fire Scene-view shortcuts (frame-selected, gizmo keys) and maximize the window
  (<kbd>Shift</kbd>+<kbd>Space</kbd>). The Terminal tab now captures the keyboard while focused.
- **`agen_set_component_properties` / `agen_manage_component` / `agen_get_gameobject` on `Transform`
  (and any other short type name that collides across loaded assemblies).** Resolving the bare name
  `Transform` no longer fails with `type 'Transform' is ambiguous` when RadeonRays / log4net types are
  loaded — short names now resolve to the canonical `UnityEngine` type.

### Docs
- Added `KNOWN_ISSUES.md` listing deferred bugs and limitations (e.g. a deliberate window
  maximize/restore drops the terminal session; mouse clicks/drags aren't forwarded to the CLI).

## [0.2.0] - 2026-07-06

### Added
- **Editor-control tools** so the CLI drives Unity through first-class tools instead of writing and
  compiling throwaway editor scripts, and checks the Editor instead of asking the user:
  - GameObjects — `agen_create_gameobject` (empty/primitive/prefab/copy), `agen_modify_gameobject`
    (rename/reparent/active/tag/layer/static/transform), `agen_delete_gameobjects`,
    `agen_find_gameobjects` (name/path/component/tag), `agen_get_gameobject` (full serialized
    component data).
  - Components — `agen_manage_component` (add/remove) and `agen_set_component_properties`, a
    SerializedProperty-first setter (Inspector-accurate, Undo-able) with a reflection fallback,
    handling Vector/Color/Quaternion/enum/object-reference/array/nested-struct values.
  - Scenes — `agen_manage_scene` (save/open/create; dirty-guarded, play-mode aware).
  - Assets — `agen_manage_asset` (GUID-safe move/copy/delete, create prefab from a scene object,
    create material).
  - Editor — `agen_execute_menu_item`, `agen_playmode`, `agen_set_selection`.
  - `agen_capture_screenshot` — Game/Scene view to a PNG the CLI reads back.
  - `agen_run_tests` — Test Runner (EditMode/PlayMode) via start → poll → report.
  - `agen_execute_code` — compile & run a C# snippet in the Editor (OFF by default; enable in
    Settings ▸ "Allow code execution").
- Scene-reads now return **instanceIDs** for unambiguous targeting: `agen_get_scene_hierarchy`
  gains instanceID + path per node, and `agen_get_project_info` gains a loaded-scenes list.
- A strong tool-use system prompt (`--append-system-prompt`, capability-sniffed) plus rewritten
  `AGENTS.md` hard rules that steer the CLI to use tools over scripts/questions.

### Changed
- Tool descriptions rewritten to cross-route and to prefer querying the Editor over asking the user.
- The bridge client retries transient connect failures during domain-reload rebinds, so a tool
  call landing mid-reload rides it out instead of erroring.

### Fixed
- A missing/unbuilt MCP server no longer starts the CLI silently without tools: the Terminal tab
  shows a loud banner and the console logs an error (both the Claude and Antigravity launch paths).

## [0.1.1] - 2026-06-26

### Added
- `install/setup.cmd` — a double-clickable launcher for the one-time setup. It runs the build
  script with `-ExecutionPolicy Bypass` (so the Windows "downloaded from the internet" script
  block can't stop it) and keeps the window open so the result is readable.

### Changed
- Moved the setup engine to `install/lib/setup.ps1` so the `install/` folder shows only the
  double-clickable `setup.cmd` — clearer which file to run.
- Removed the redundant `Window/Agen-Link/Rebuild Neuron Graph` menu item; rebuilding the Neuron
  graph lives on the Neuron tab's **⟳ Rebuild** button (which also respects the folder filter and
  guards against concurrent rebuilds).
- Overhauled the **README** for the public landing page (About / Requirements / Install, with
  in-Editor screenshots and side-by-side Claude / Antigravity terminals) and clarified **INSTALL.txt**.

### Fixed
- Setup no longer fails silently when launched by double-clicking / "Run with PowerShell" on a
  freshly downloaded `setup.ps1`: the script now reports errors clearly and pauses instead of
  closing the window instantly. `*.cmd`/`*.bat` are forced to CRLF via `.gitattributes` so the
  source ZIP runs correctly.

## [0.1.0] - 2026-06-24

Initial public release.

### Added
- **Embedded terminal** running the real **Claude Code** or Google **Antigravity** (`agy`) CLI
  inside a Unity Editor window. Sessions survive script recompiles (domain reloads) via a
  detached Windows ConPTY host with per-session token auth and ring-buffer replay.
- **Live MCP bridge** (`127.0.0.1`) exposing the open Editor to the CLI: project info, Console
  logs, compile errors, asset refresh, scene hierarchy, selection, and asset search
  (`agen_get_project_info`, `agen_read_console`, `agen_get_compile_errors`, `agen_refresh_assets`,
  `agen_get_scene_hierarchy`, `agen_get_selection`, `agen_find_assets`).
- **Analysis tab** — one-click scene + asset optimization audit, play-mode performance
  profiling, and whitelisted, Undo-able auto-fixes (`agen_audit_scene`, `agen_audit_assets`,
  `agen_perf_*`, `agen_apply_fixes`).
- **History tab** — read-only browser of past AI conversations for the project, grouped by date.
- **Neuron tab** — a live, Assets-only knowledge graph of scripts/prefabs/scenes, auto-grouped
  into named systems (`agen_graph_*`).
- **GitHub tab** — whole-project backup with browser sign-in.
- **Shared project memory** across both CLIs (`AGENTS.md` + `agen_memory_*`).
- One-time `install/` build script and full `INSTALL.txt` / `README.md` docs.

### Security
- All listeners bind to localhost only; terminal host uses per-session token authentication.

[Unreleased]: https://github.com/HardikBasera/Agen-Link/compare/v0.3.2...HEAD
[0.3.2]: https://github.com/HardikBasera/Agen-Link/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/HardikBasera/Agen-Link/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/HardikBasera/Agen-Link/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/HardikBasera/Agen-Link/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/HardikBasera/Agen-Link/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/HardikBasera/Agen-Link/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/HardikBasera/Agen-Link/releases/tag/v0.1.0
