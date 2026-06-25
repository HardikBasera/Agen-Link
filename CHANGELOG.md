# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/HardikBasera/Agen-Link/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/HardikBasera/Agen-Link/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/HardikBasera/Agen-Link/releases/tag/v0.1.0
