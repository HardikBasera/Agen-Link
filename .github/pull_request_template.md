<!-- Thanks for contributing to Agen-Link! Please fill out the checklist below. -->

## What does this PR do?

<!-- A short description of the change and why it's needed. Link any related issue: Fixes #123 -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Docs only
- [ ] Refactor / chore

## Checklist

- [ ] I read [CONTRIBUTING.md](../blob/main/CONTRIBUTING.md).
- [ ] My commits are signed off (`git commit -s`) per the DCO.
- [ ] **If I changed `mcp-server/src/`, I ran `npm run build` and committed the regenerated
      `mcp-server/build/`** (CI checks this).
- [ ] I did **not** add any `preinstall` / `postinstall` / `prepare` script to a `package.json`.
- [ ] I did **not** add new dependencies without justification (and the lockfile is updated).
- [ ] The bridge still binds to localhost only; no new tool runs arbitrary shell/file writes.
- [ ] No secrets, tokens, or personal/machine-specific paths are included.
- [ ] Unity `.meta` files are present for any added/moved files under `unity-package/`.
- [ ] I tested the change (describe how below) and updated docs/`CHANGELOG.md` if relevant.

## How was this tested?

<!-- e.g. ran `npm test` in pty-host; loaded the package in Unity 2022.3 and exercised X -->
