# Security Policy

Thanks for helping keep Agen-Link and its users safe.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately through GitHub's **"Report a vulnerability"** button on the
repository's **Security** tab (Security → Advisories → Report a vulnerability).
This opens a private advisory visible only to you and the maintainer.

Include, where possible:

- a description of the issue and its impact,
- the affected component (`unity-package/`, `pty-host/`, `mcp-server/`, or `install/`),
- steps to reproduce or a proof of concept,
- the version / commit you tested.

**Response targets** (best effort, this is a solo-maintained project):

- acknowledgement within **5 business days**,
- an initial assessment within **10 business days**,
- a fix or mitigation plan communicated in the private advisory before any public disclosure.

Please give a reasonable amount of time for a fix before public disclosure. Credit will be
given in the advisory unless you prefer to remain anonymous.

## Supported versions

This project is pre-1.0 and ships from `main`. Security fixes are applied to the latest
release and `main` only.

| Version | Supported |
|--------:|:---------:|
| latest `0.1.x` / `main` | ✅ |
| older   | ❌ |

## Security model (what to keep in mind)

Agen-Link runs a real AI CLI and a bridge into the live Unity Editor on your machine. Its
design keeps that local:

- **Localhost only.** The Editor bridge listens on `127.0.0.1` (default port `6577`) and the
  terminal host on an ephemeral `127.0.0.1` port. Nothing binds to a network-facing interface.
  **Do not reconfigure the bridge to bind to `0.0.0.0` or any non-loopback address** — that
  would expose the Editor to your network with no authentication.
- **Terminal-host auth.** The pty-host requires a per-session token (a fresh GUID) before it
  accepts a client.
- **The CLI has your access.** The embedded terminal runs the *real* Claude / Antigravity CLI
  with your own login and configuration. The Unity bridge only **adds** live-editor awareness;
  it does not sandbox the CLI. If your CLI account or machine is compromised, the bridge is too.
- **One write-capable tool.** Most `agen_*` tools are read-only. `agen_apply_fixes` applies
  whitelisted scene/asset optimization fixes — review audit findings before applying. Scene
  changes are Undo-able and not auto-saved; asset-import changes reimport immediately.
- **Don't put secrets where the bridge can read them.** The bridge can read the Unity Console;
  avoid logging API keys or tokens. Keep secrets in git-ignored `.env`-style files loaded at
  runtime.

## Scope

In scope: the code in this repository (`unity-package/`, `pty-host/`, `mcp-server/`,
`install/`). Out of scope: vulnerabilities in third-party dependencies (report those upstream;
we track them via Dependabot), in the Claude / Antigravity CLIs themselves, or in Unity.
