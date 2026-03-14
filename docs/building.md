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
# All tests
dotnet test tests/Leviathan.Core.Tests/Leviathan.Core.Tests.csproj

# Single test class
dotnet test tests/Leviathan.Core.Tests/ --filter "FullyQualifiedName~PieceTreeTests"

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

## Publish Native AOT Binaries

TUI2 and TUI support `PublishAot=true`. AOT compilation requires building on the target OS.

**Windows (x64):**

```powershell
dotnet publish src/Leviathan.TUI2/Leviathan.TUI2.csproj -c Release -r win-x64
```

**Linux (x64):**

```bash
dotnet publish src/Leviathan.TUI2/Leviathan.TUI2.csproj -c Release -r linux-x64
```

Output lands in `src/Leviathan.TUI2/bin/Release/net10.0/{rid}/publish/`.

> **Note:** The GUI project (`Leviathan.UI`) has `PublishAot=false` — use standard
> `dotnet publish` without AOT for that frontend.

## CI/CD

The project uses GitHub Actions for continuous integration and releases:

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | Push to any branch, PRs to main | Builds solution + runs tests |
| `prerelease.yml` | Push to any branch | AOT builds for win-x64 + linux-x64, creates rolling pre-release |
| `release.yml` | Push of `v*` tag | AOT builds + official GitHub Release |

Versioning is managed by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
in the TUI2 project.

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
| `src/Leviathan.UI` | Exe | ❌ | GUI (ImGui/OpenGL) |
| `src/Leviathan.TUI` | Exe | ✅ | Terminal UI (Hex1b) |
| `tests/Leviathan.Core.Tests` | Test | — | xUnit tests for Core |
| `tests/Leviathan.TUI.Tests` | Test | — | xUnit tests for TUI |
