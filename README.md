[![CI](https://github.com/lstefano71/Leviathan/actions/workflows/ci.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/ci.yml) [![Pre-release](https://github.com/lstefano71/Leviathan/actions/workflows/prerelease.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/prerelease.yml) [![Release TUI2](https://github.com/lstefano71/Leviathan/actions/workflows/release-tui2.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/release-tui2.yml) [![Release GUI](https://github.com/lstefano71/Leviathan/actions/workflows/release-gui.yml/badge.svg)](https://github.com/lstefano71/Leviathan/actions/workflows/release-gui.yml)

# Leviathan

A native-AOT hex + text + CSV editor that opens **50 GB+ files in under 500 ms** with **zero GC allocations** in the render loop. Built with C# / .NET 10 for security analysts, DevOps engineers, and anyone who needs to inspect massive files without waiting.

## ✨ Features

- **50 GB+ file support** — memory-mapped I/O, no file size limits
- **< 500 ms open time** — instant access to any file, regardless of size
- **Zero-allocation render loop** — no GC pauses, smooth 60 fps scrolling
- **Hex + Text + CSV views** — three viewing modes in one editor
- **SIMD-accelerated line indexing** — AVX2/SSE2/NEON cascade scans at GB/s speeds
- **Multi-encoding support** — UTF-8, UTF-16 LE, Windows-1252 with automatic detection
- **Boyer-Moore-Horspool search** — fast pattern matching across the entire file, text and hex patterns, streaming results, regex and whole-word support
- **Atomic save** — edits are never lost, even on crash during save
- **Native AOT** — single-file executable, no runtime required
- **Red-Black piece table** — O(log N) insert/delete at any position
- **Full theme system** — built-in Dark / Light / GreenPhosphor themes, live theme editor, import/export JSON themes
- **Undo / redo** — multi-level edit history

## 🖥️ Front-ends

### Desktop GUI

- **Three view modes** — Hex (address/hex/ASCII, cursor, selection, copy-as-hex), Text (encoding-aware, word wrap), CSV (tabular grid, sticky header, column visibility)
- **Command Palette** (`Ctrl+P`) — VS Code-style fuzzy launcher with recently-used section
- **Find Bar** (`Ctrl+F`) — text/hex search, regex, whole-word, case toggle, streaming match highlights
- **Theme Editor** — live-preview color editor with a built-in color picker, Duplicate-from-built-in workflow, import/export JSON themes
- **Linked view tabs** — open the same file in Hex + Text side-by-side with synchronized scrolling
- Drag-and-drop, MRU file list, font picker, interactive status bar

### Terminal UI (TUI2)

Built on [Terminal.Gui 2.x](https://github.com/gui-cs/Terminal.Gui) — runs in any modern terminal on Windows, macOS, and Linux.

- **Three view modes** — Hex, Text, and CSV
- **Command Palette**, **Find Bar**, **Go-to Bar**
- **CSV tools** — dialect detection (comma/tab/semicolon/pipe), header detection, record detail dialog

## 📦 Quick Start

```bash
# Run the desktop GUI
dotnet run --project src/Leviathan.GUI -- path/to/large-file

# Or use the terminal UI (any modern terminal)
dotnet run --project src/Leviathan.TUI2 -- path/to/large-file

# Publish a native AOT single-file binary
dotnet publish src/Leviathan.GUI/Leviathan.GUI.csproj -c Release -r win-x64
```

## 📖 Documentation

- **[Build & Install](docs/building.md)** — prerequisites, build, test, publish AOT binaries
- **[Architecture Deep-Dive](docs/architecture.md)** — data pipeline, PieceTree, SIMD indexing, search engine, encoding detection, GUI front-end architecture
- **[Theme Guide](docs/themes.md)** — creating, editing, and installing themes
- **[Keyboard Shortcuts & Help](docs/help.md)** — full feature reference for the desktop GUI
- **[Releases](https://github.com/lstefano71/Leviathan/releases)** — download pre-built binaries

## 🏗️ Architecture at a Glance

`Document` is the public façade over `MappedFileSource` (zero-copy memory-mapped reads), `AppendBuffer` (arena allocator backed by `ArrayPool<byte>`), and `PieceTree` (red-black piece table with O(log N) positional operations). Background SIMD line indexing enables instant scrollbar positioning over arbitrarily large files. All hot-path code uses `stackalloc`, `ReadOnlySpan<byte>`, and value types — zero heap allocations per frame.

See **[Architecture Deep-Dive](docs/architecture.md)** for the full technical breakdown with diagrams.

## Frontends

| Frontend | Stack | Status | Description |
|----------|-------|--------|-------------|
| **GUI** | Desktop GUI | ✅ Primary | Full-featured desktop editor: hex/text/CSV views, theme editor, command palette, find bar, linked tabs |
| **TUI2** | Terminal.Gui 2.x | ✅ Terminal | Full-featured terminal UI: hex/text/CSV views, command palette, find bar |
| TUI | Hex1b | Legacy | Minimal ANSI-based terminal UI |

## Contributing

Contributions welcome. Keep changes focused, preserve hot-path performance characteristics, and add tests in `tests/Leviathan.Core.Tests` for core changes. See **[Build & Install](docs/building.md)** for development setup.

## License

Unlicense — see [`LICENSE.txt`](LICENSE.txt).
