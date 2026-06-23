# Agen-Link

Run an AI coding CLI **inside the Unity Editor**. Agen-Link embeds a real terminal — running either
the **Claude Code** CLI or Google's **Antigravity** CLI — in a Unity Editor window, and wires it to a
**live MCP bridge** so the AI can read and act on the open Editor: Console logs, compile errors, the
scene hierarchy, assets, a project knowledge graph, and a scene-optimization auditor. It also lets you
browse your past AI sessions, map your project as a graph, and back the whole project up to GitHub.

Unlike the usual setup where an external chat app drives Unity over MCP, Agen-Link **inverts it**:
*Unity hosts the CLI*, and the MCP server is the AI's window back into the live Editor.

> **Platform:** Windows 10/11. **Unity:** 2021.3+ (tested on 2022.3 LTS and Unity 6).

## The window

`Window ▸ Agen-Link` opens a six-tab panel:

- **Terminal** — the embedded AI CLI (Claude or Antigravity). Your own login, skills, plugins and
  slash-commands all apply; the Unity bridge is added on top. The session **survives script
  recompiles** — it reconnects and replays.
- **Analysis** — one-click scene + asset optimization audit (per-renderer polycounts, realtime
  lights, batching, textures, audio, …), play-mode performance profiling, and safe auto-fixes
  (Undo-able scene changes; flagged asset reimports).
- **History** — a read-only browser of your past AI conversations for this project, grouped by date.
- **Neuron** — a live, Assets-only knowledge graph of your scripts / prefabs / scenes and how they
  wire together, auto-grouped into named "systems".
- **GitHub** — whole-project backup with a browser sign-in (no passwords typed into Unity).
- **Settings** — choose the CLI, font size, bridge port, and tool paths.

## Requirements

| Tool | Why |
|------|-----|
| **Node.js 18+** | runs the MCP server and the terminal host |
| **An AI CLI** | **Claude Code** (`npm i -g @anthropic-ai/claude-code`) and/or Google **Antigravity** (`agy`) |
| **Unity 2021.3+** | the editor |
| **git** + **GitHub CLI** (`gh`) | optional — for the GitHub backup tab |

The package also pulls in **Newtonsoft.Json** (`com.unity.nuget.newtonsoft-json`) automatically.

## Install

1. Clone this repo, then run the one-time setup (builds the MCP server + terminal host, installs `gh`):
   ```powershell
   powershell -ExecutionPolicy Bypass -File "path\to\Agen-Link\install\setup.ps1"
   ```
2. In your Unity project: `Window ▸ Package Manager ▸ + ▸ Add package from disk…` and pick
   `path\to\Agen-Link\unity-package\package.json`.
3. Open `Window ▸ Agen-Link`. The Console should log `[Agen-Link] Listening on 127.0.0.1:6577`.

See **INSTALL.txt** for the full step-by-step guide (including letting the CLI install the
prerequisites for you).

> **Note:** the native `node-pty` and the MCP server build are machine-specific and gitignored — run
> `install/setup.ps1` after cloning (and don't move/rename the folder afterward; helpers are found
> relative to `unity-package/`).

## How it works

Three processes keep the AI session alive across Unity's domain reloads (recompiles):

- A tiny **TCP bridge** (`127.0.0.1:6577`) runs inside the Editor and executes every request on the
  main thread. The **Node MCP server** connects to it and exposes live-editor tools to the CLI:
  `agen_get_project_info`, `agen_read_console`, `agen_get_compile_errors`, `agen_refresh_assets`,
  `agen_get_scene_hierarchy`, `agen_get_selection`, `agen_find_assets`, the scene-analysis tools
  (`agen_audit_scene`, `agen_audit_assets`, `agen_perf_*`, `agen_apply_fixes`), the Neuron graph
  tools (`agen_graph_*`), and shared project-memory tools (`agen_memory_*`).
- The **Terminal** launches the CLI through a detached **pty-host** (Node + Windows ConPTY) that the
  Editor talks to over a localhost socket. It authenticates with a per-session token, replays a ring
  buffer on reconnect, and watches the parent Editor PID — so the session survives recompiles and is
  torn down when Unity exits.
- Both CLIs share one **local project memory** (an `AGENTS.md` plus the `agen_memory_*` tools), so you
  don't have to re-explain the project when you switch between them.

## Scope & safety

- The Terminal runs the **real** AI CLI with your own configuration — it has whatever access your
  normal CLI does. The Unity bridge only **adds** live-editor awareness; it doesn't restrict the CLI.
- Your safety nets are **git** (the GitHub tab) and Unity's own Undo. Scene auto-fixes are Undo-able
  and never auto-saved; asset-import fixes reimport immediately (and are flagged as permanent).
- The bridge listens only on **localhost** (`127.0.0.1`) and is skipped in Asset Import Worker processes.

## Troubleshooting

- **"Failed to start on port 6577"** → another Editor may hold the port; change it in **Settings ▸ Bridge server**.
- **Terminal: "pty-host is not built"** → run `install/setup.ps1` (installs `node-pty`), then restart Unity.
- **"MCP server not found"** → run `install/setup.ps1` (or `npm run build` in `mcp-server/`); set the path in Settings.
- **GitHub: `gh` not found** → run `install/setup.ps1`, then restart Unity so it picks up the new PATH.

## Layout

```
unity-package/   Unity package (Editor C#): bridge, window, Terminal, Analysis, History, GitHub, Neuron graph
pty-host/        Node terminal host (Windows ConPTY) for the embedded Terminal
mcp-server/      Node / TypeScript MCP server (src/ → build/)
install/         setup.ps1 (one-time build + gh install)
```
