# Building Leviathan

## Prerequisites

- **.NET 10 SDK** — [download here](https://dotnet.microsoft.com/download/dotnet/10.0)
- The solution uses the **`.slnx`** format, which requires Visual Studio 17.10+ or .NET SDK 9.0.200+.

## Build the Solution

```powershell
dotnet build Leviathan.slnx
dotnet build Leviathan.slnx -c Release
```

## Run Tests

```powershell
# All tests in solution
dotnet test Leviathan.slnx

# Per-frontend test projects
dotnet test tests/Leviathan.Core.Tests/Leviathan.Core.Tests.csproj
dotnet test tests/Leviathan.TUI.Tests/Leviathan.TUI.Tests.csproj
dotnet test tests/Leviathan.GUI.Tests/Leviathan.GUI.Tests.csproj

# Single test method
dotnet test tests/Leviathan.Core.Tests/ --filter "FullyQualifiedName~PieceTreeTests.Insert_AtBeginning_ReturnsCorrectContent"
```

## Run the Frontends

### GUI (Avalonia desktop)

```powershell
dotnet run --project src/Leviathan.GUI
dotnet run --project src/Leviathan.GUI -- path/to/file
```

### TUI2 (Terminal.Gui — recommended terminal UI)

```powershell
dotnet run --project src/Leviathan.TUI
dotnet run --project src/Leviathan.TUI -- path/to/file
```

## Publish Frontend Binaries

TUI2 and GUI are both versioned with Nerdbank.GitVersioning and published as release artifacts.
Publishing should run on the target OS for each RID.

**Windows (x64):**

```powershell
dotnet publish src/Leviathan.TUI2/Leviathan.TUI2.csproj -c Release -r win-x64
dotnet publish src/Leviathan.GUI/Leviathan.GUI.csproj -c Release -r win-x64
```

**Linux (x64):**

```bash
dotnet publish src/Leviathan.TUI2/Leviathan.TUI2.csproj -c Release -r linux-x64
dotnet publish src/Leviathan.GUI/Leviathan.GUI.csproj -c Release -r linux-x64
```

Outputs land in:
- `src/Leviathan.TUI2/bin/Release/net10.0/{rid}/publish/`
- `src/Leviathan.GUI/bin/Release/net10.0/{rid}/publish/`

## CI/CD

The project uses GitHub Actions for continuous integration and releases:

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | Push to any branch, PRs to main | Builds solution + runs Core/TUI/GUI tests |
| `prerelease.yml` | Push to non-`main` branches | Builds TUI2 + GUI for win-x64/linux-x64 and creates rolling branch pre-releases |
| `release-tui2.yml` | Push of `tui2/v*` tag | Builds TUI2 for win-x64/linux-x64 and creates official TUI2 GitHub Release |
| `release-gui.yml` | Push of `gui/v*` tag | Builds GUI for win-x64/linux-x64 and creates official GUI GitHub Release |

Versioning is managed by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
in both `src/Leviathan.TUI2` and `src/Leviathan.GUI`.

**To create an official release**, use the coordinated release script:

```bash
# Release TUI2 only
./scripts/release-coordinated.sh --tui2 0.2.2

# Release GUI only
./scripts/release-coordinated.sh --gui 0.4.1

# Release both at the same time
./scripts/release-coordinated.sh --tui2 0.2.2 --gui 0.4.1
```

Or in PowerShell:

```powershell
.\scripts\release-coordinated.ps1 -GuiVersion 0.4.1
.\scripts\release-coordinated.ps1 -Tui2Version 0.2.2 -GuiVersion 0.4.1
```

The scripts bump `version.json`, commit, create the product-scoped tag (`tui2/vX.Y.Z` or `gui/vX.Y.Z`), and push. See **[Release Process](release.md)** for details.

## Project Structure

| Project | Type | AOT | Description |
|---|---|---|---|
| `src/Leviathan.Core` | Library | Compatible | Headless data engine |
| `src/Leviathan.GUI` | Exe | ✅ | Avalonia desktop frontend |
| `src/Leviathan.TUI2` | Exe | ✅ | Terminal UI (Terminal.Gui) |
| `src/Leviathan.UI` | Exe | ✅ | ImGui/OpenGL frontend (legacy) |
| `src/Leviathan.TUI` | Exe | ✅ | Terminal UI (Hex1b, legacy) |
| `tests/Leviathan.Core.Tests` | Test | — | xUnit tests for Core |
| `tests/Leviathan.GUI.Tests` | Test | — | xUnit tests for GUI |
| `tests/Leviathan.TUI.Tests` | Test | — | xUnit tests for TUI |

## Multi-artifact release best practices (used here)

- Keep one GitHub Release per version, with separate assets per product and RID.
- Use explicit, predictable asset names (for example: `leviathan-gui_{version}_win-x64.zip`).
- Publish checksums that cover all artifacts in the release.
- Use stable tags (`v*`) for immutable releases and rolling pre-release tags per branch for manual tester validation.
- Prepend short human-readable highlights in release notes before detailed generated changelog content.
