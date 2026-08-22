# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/HardikBasera/Agen-Link/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/HardikBasera/Agen-Link/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/HardikBasera/Agen-Link/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/HardikBasera/Agen-Link/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/HardikBasera/Agen-Link/releases/tag/v0.1.0
