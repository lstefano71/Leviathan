# Release process

This repository builds two independent products from the same source tree — `Leviathan.TUI2` and `Leviathan.GUI` — both backed by `Leviathan.Core`.

## Independent release cycles

Each product has its own version number in its `version.json` and its own release tag namespace:

| Product | `version.json` | Tag namespace | Workflow |
|---|---|---|---|
| TUI2 | `src/Leviathan.TUI2/version.json` | `tui2/v*` | `release-tui2.yml` |
| GUI  | `src/Leviathan.GUI/version.json`  | `gui/v*`  | `release-gui.yml`  |

Pushing a `tui2/v*` tag triggers **only** the TUI2 release workflow. Pushing a `gui/v*` tag triggers **only** the GUI release workflow. The two products can be released at completely different cadences.

[Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) computes each product's version from its `version.json` plus the number of commits since the last tag in its own namespace. Bumping and tagging one product has zero effect on the other's commit height.

## Releasing

Use the release script, which bumps `version.json`, commits, creates the product-scoped tag, and pushes:

**POSIX:**
```bash
# TUI2 only
./scripts/release-coordinated.sh --tui2 0.2.2

# GUI only
./scripts/release-coordinated.sh --gui 0.4.0

# Both at once (coordinated release)
./scripts/release-coordinated.sh --tui2 0.2.2 --gui 0.4.0

# Preview without committing
./scripts/release-coordinated.sh --tui2 0.2.2 --dry-run
```

**PowerShell:**
```powershell
# TUI2 only
.\scripts\release-coordinated.ps1 -Tui2Version 0.2.2

# GUI only
.\scripts\release-coordinated.ps1 -GuiVersion 0.4.0

# Both at once
.\scripts\release-coordinated.ps1 -Tui2Version 0.2.2 -GuiVersion 0.4.0

# Preview without committing
.\scripts\release-coordinated.ps1 -Tui2Version 0.2.2 -DryRun

# Sign tags with GPG
.\scripts\release-coordinated.ps1 -Tui2Version 0.2.2 -Sign
```

Or manually, if you prefer:
```bash
# Edit src/Leviathan.TUI2/version.json, bump "version" field, then:
git add src/Leviathan.TUI2/version.json
git commit -m "Bump tui2 to 0.2.2"
git tag -a tui2/v0.2.2 -m "Release tui2/v0.2.2"
git push origin --follow-tags
```

## When CI runs

- **`ci.yml`** — runs on every push to `main` and on PRs when files under `src/**` or `tests/**` change.
- **`prerelease.yml`** — runs on every non-`main` branch push; builds both products and creates per-product rolling pre-release entries (`pre-<branch>-tui2`, `pre-<branch>-gui`).
- **`release-tui2.yml`** — runs on `tui2/v*` tag pushes; builds a production TUI2 release.
- **`release-gui.yml`** — runs on `gui/v*` tag pushes; builds a production GUI release.
