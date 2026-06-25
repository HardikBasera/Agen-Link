# Contributing to Agen-Link

Thanks for your interest in improving Agen-Link! This guide covers how to propose changes,
the local dev setup, and the few project-specific rules that keep things safe and reproducible.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

- **Bug reports & feature requests** — open an [Issue](../../issues/new/choose) using the
  templates. For open-ended questions or ideas, use **Discussions**.
- **Code / docs** — open a Pull Request (see below).

## Pull request workflow

You can't push to this repository directly — contributions come through forks:

1. **Fork** the repo and clone your fork.
2. Create a branch: `git checkout -b fix/short-description`.
3. Make your change, following the conventions below.
4. **Build and test locally** (see Development setup).
5. Commit with a clear message and a `Signed-off-by` line (see [DCO](#developer-certificate-of-origin-dco)).
6. Push to your fork and open a **Pull Request against `main`**. Fill in the PR template.
7. CI runs on your PR (a maintainer may need to approve the first run from a new contributor).
   Address review feedback; once approved and green, the maintainer squash-merges.

Keep PRs focused — one logical change per PR is much easier to review and merge.

## Development setup

Prerequisites: **Windows 10/11**, **Node.js 18+**, **Unity 2021.3+**, and (for testing the
terminal) the **Claude Code** and/or **Antigravity** CLI. See `INSTALL.txt` for the full list.

```powershell
# One-time build of both Node helpers (installs deps, rebuilds native node-pty, builds mcp-server)
powershell -ExecutionPolicy Bypass -File "install\lib\setup.ps1"
```

To test inside Unity: `Window ▸ Package Manager ▸ + ▸ Add package from disk…` →
`unity-package/package.json`, then open `Window ▸ Agen-Link`.

> After changing any `agen_*` **tool description** in `mcp-server`, **restart the terminal
> session** so the CLI picks up the new descriptions.

## Project conventions (please read)

- **Rebuild and commit `mcp-server/build/`.** The compiled JS in `mcp-server/build/` is
  committed so users can install without a TypeScript toolchain. If you change anything under
  `mcp-server/src/`, run `npm run build` in `mcp-server/` and commit the regenerated `build/`.
  **CI fails if `build/` is out of sync with `src/`.**
- **No install lifecycle scripts.** Do **not** add `preinstall`, `postinstall`, or `prepare`
  scripts to any `package.json`. They execute arbitrary code on every `npm install` and will
  be rejected.
- **New dependencies need justification.** Prefer the standard library. If you add a dependency,
  explain why in the PR, keep it permissively licensed (MIT/Apache/BSD/ISC), and commit the
  updated lockfile.
- **Keep the bridge localhost-only.** PRs that make the listener bind to a non-loopback address,
  or that add an `agen_*` tool running arbitrary shell commands or unrestricted file writes
  (beyond the existing whitelist), will not be accepted.
- **Unity `.meta` files.** Every asset/script under `unity-package/` has a paired `.meta` file —
  add/keep them when you add or move files.
- **Don't commit local/secret files.** No `.env`, tokens, personal paths, or machine-specific
  config. `node_modules/`, `AgenLink~/`, and local settings are already gitignored.
- **Match the surrounding style.** C# follows the existing Editor code; TypeScript/JS follows the
  existing helper style. Keep changes minimal and consistent.

## Tests

- `pty-host`: `npm test` (Node's built-in test runner; ConPTY protocol round-trips).
- `unity-package`: EditMode tests live under the `*.Tests` asmdefs and run via Unity's Test Runner.

Add or update tests when you change behavior.

## Developer Certificate of Origin (DCO)

To certify that you wrote the patch or otherwise have the right to submit it under the project
license, **sign off** each commit:

```
git commit -s -m "Your message"
```

This appends a `Signed-off-by: Your Name <you@example.com>` line, certifying the
[DCO](https://developercertificate.org/). Contributions are accepted under the project's
**Apache-2.0** license (the patent grant in Apache-2.0 §5 covers contributions — no separate CLA
is required).

> This is requested as good practice but is **not** automatically enforced — there's no bot
> blocking your PR if you forget. If you do, you can add it afterwards with
> `git commit --amend -s` (single commit) or `git rebase --signoff main` (multiple commits).

## License

By contributing, you agree that your contributions will be licensed under the
[Apache License 2.0](LICENSE).
