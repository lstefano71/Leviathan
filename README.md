[![CI](https://github.com/lstefano71/Leviathan/actions/workflows/ci.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/ci.yml) [![Pre-release](https://github.com/lstefano71/Leviathan/actions/workflows/prerelease.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/prerelease.yml) [![Release workflow](https://github.com/lstefano71/Leviathan/actions/workflows/release.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/release.yml)

# Leviathan

A native-AOT hex + text + CSV editor that opens **50 GB+ files in under 500 ms** with **zero GC allocations** in the render loop. Built with C# / .NET 10 for security analysts, DevOps engineers, and anyone who needs to inspect massive files without waiting.

## ✨ Features

- **50 GB+ file support** — memory-mapped I/O, no file size limits
- **< 500 ms open time** — instant access to any file, regardless of size
- **Zero-allocation render loop** — no GC pauses, smooth 60 fps scrolling
- **Hex + Text + CSV views** — three viewing modes in one editor
- **SIMD-accelerated line indexing** — AVX2/SSE2/NEON cascade scans at GB/s speeds
- **Multi-encoding support** — UTF-8, UTF-16 LE, Windows-1252 with automatic detection
- **Boyer-Moore-Horspool search** — fast pattern matching across the entire file, text and hex patterns
- **Atomic save** — edits are never lost, even on crash during save
- **Native AOT** — single-file executable, no runtime required
- **Red-Black piece table** — O(log N) insert/delete at any position

## 🖥️ Leviathan TUI2

**The recommended way to use Leviathan.** Built on [Terminal.Gui 2.x](https://github.com/gui-cs/Terminal.Gui), it runs in any modern terminal on Windows, macOS, and Linux.

- **Three view modes** — Hex view (address/hex/ASCII columns, cursor navigation, editing, selection), Text view (encoding-aware, word wrap via `LineWrapEngine`, visual lines), CSV view (tabular grid, sticky header, cell cursor, row selection)
- **Command Palette** (`Ctrl+P`) — VS Code-style fuzzy-filtered command launcher with categories and keyboard shortcuts
- **Find Bar** — VS Code-style find: text search, case toggle `[Aa]`, hex pattern toggle `[Hx]`, match counter, prev/next navigation
- **Go-to Bar** — jump to any offset or line number instantly
- **CSV tools** — dialect detection (comma/tab/semicolon/pipe), header detection, record detail dialog

## 📦 Quick Start

```bash
# Run TUI2 (recommended)
dotnet run --project src/Leviathan.TUI2 -- path/to/large-file

# Or publish a native AOT binary
dotnet publish src/Leviathan.TUI2/Leviathan.TUI2.csproj -c Release -r win-x64
```

## 📖 Documentation

- **[Build & Install](docs/building.md)** — prerequisites, build, test, publish AOT binaries
- **[Architecture Deep-Dive](docs/architecture.md)** — data pipeline, PieceTree, SIMD indexing, search engine, encoding detection
- **[Releases](https://github.com/lstefano71/Leviathan/releases)** — download pre-built binaries

## 🏗️ Architecture at a Glance

`Document` is the public façade over `MappedFileSource` (zero-copy memory-mapped reads), `AppendBuffer` (arena allocator backed by `ArrayPool<byte>`), and `PieceTree` (red-black piece table with O(log N) positional operations). Background SIMD line indexing enables instant scrollbar positioning over arbitrarily large files. All hot-path code uses `stackalloc`, `ReadOnlySpan<byte>`, and value types — zero heap allocations per frame.

See **[Architecture Deep-Dive](docs/architecture.md)** for the full technical breakdown with diagrams.

## Frontends

| Frontend | Stack | Status | Description |
|----------|-------|--------|-------------|
| **TUI2** | Terminal.Gui 2.x | ✅ Primary | Full-featured terminal UI with hex/text/CSV views, command palette, find bar |
| GUI | ImGui + OpenGL | Functional | Immediate-mode desktop GUI via Silk.NET |
| TUI | Hex1b | Original | Minimal ANSI-based terminal UI |

## Contributing

Contributions welcome. Keep changes focused, preserve hot-path performance characteristics, and add tests in `tests/Leviathan.Core.Tests` for core changes. See **[Build & Install](docs/building.md)** for development setup.

## License

Unlicense — see [`LICENSE.txt`](LICENSE.txt).
