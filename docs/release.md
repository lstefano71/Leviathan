# Release process

This repository contains multiple products built from the same source tree: `Leviathan.GUI` and `Leviathan.TUI2`, which both depend on `Leviathan.Core`.

Summary:
- CI runs are scoped by path so unrelated changes (docs, etc.) don't trigger expensive builds.
- `GUI` and `TUI2` can be released independently. A coordinated release script is provided for simultaneous releases.

When CI runs
- The main CI (`.github/workflows/ci.yml`) runs only when files under `src/**` or `tests/**` change.
- The pre-release workflow (`.github/workflows/prerelease.yml`) still builds artifacts, but the version-parity check only runs when both `GUI` and `TUI2` are changed in the same push/PR, or when a `release/*` branch is used.
- The release workflow (`.github/workflows/release.yml`) runs on tag pushes (`v*`) and publishes artifacts for both products.

Coordinated release
- Use the provided scripts to bump both `version.json` files and create a `vX.Y.Z` tag.

POSIX:
```
./scripts/release-coordinated.sh 0.3.2
```

PowerShell:
```
.\scripts\release-coordinated.ps1 -Version 0.3.2
```

Use `--dry-run` (POSIX) or `-DryRun` (PowerShell) to preview file updates without committing.

Notes
- Coordinated releases create a tag `vX.Y.Z` which triggers the `release.yml` workflow to build and publish artifacts.
- If you prefer to release only one product, bump that product's `version.json` manually and open a PR; parity checks will not block unless both products were changed.
