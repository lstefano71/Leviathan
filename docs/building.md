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

### TUI2 (Terminal.Gui — recommended)

```powershell
dotnet run --project src/Leviathan.TUI2
dotnet run --project src/Leviathan.TUI2 -- path/to/file
```

### GUI (ImGui/OpenGL)

```powershell
dotnet run --project src/Leviathan.UI
dotnet run --project src/Leviathan.UI -- path/to/file
```

### TUI (original, Hex1b-based)

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
| `prerelease.yml` | Push to non-`main` branches | Builds TUI2 + GUI for win-x64/linux-x64 and creates rolling branch pre-release |
| `release.yml` | Push of `v*` tag | Builds TUI2 + GUI for win-x64/linux-x64 and creates official GitHub Release |

Versioning is managed by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
in both `src/Leviathan.TUI2` and `src/Leviathan.GUI`.

**To create an official release:**

```bash
git tag v0.1.0
git push --tags
```

## Project Structure

| Project | Type | AOT | Description |
|---|---|---|---|
| `src/Leviathan.Core` | Library | Compatible | Headless data engine |
| `src/Leviathan.TUI2` | Exe | ✅ | Terminal UI (Terminal.Gui) |
| `src/Leviathan.GUI` | Exe | ✅ | Avalonia desktop frontend |
| `src/Leviathan.UI` | Exe | ✅ | ImGui/OpenGL frontend |
| `src/Leviathan.TUI` | Exe | ✅ | Terminal UI (Hex1b) |
| `tests/Leviathan.Core.Tests` | Test | — | xUnit tests for Core |
| `tests/Leviathan.GUI.Tests` | Test | — | xUnit tests for GUI |
| `tests/Leviathan.TUI.Tests` | Test | — | xUnit tests for TUI |

## Multi-artifact release best practices (used here)

- Keep one GitHub Release per version, with separate assets per product and RID.
- Use explicit, predictable asset names (for example: `leviathan-gui_{version}_win-x64.zip`).
- Publish checksums that cover all artifacts in the release.
- Use stable tags (`v*`) for immutable releases and rolling pre-release tags per branch for manual tester validation.
- Prepend short human-readable highlights in release notes before detailed generated changelog content.
