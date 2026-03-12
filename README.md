# Leviathan

Leviathan is a native-AOT, immediate-mode hex + text editor designed for extremely large files (50 GB+). It focuses on minimal allocations and low-latency rendering so the UI and core operate within strict performance constraints (sub-500 ms open time and zero GC allocations in the render loop).

## Key features

- Extremely large file support (50 GB+)
- Zero-allocation hot paths in the render loop
- Memory-mapped file backing and append buffer for efficient editing
- Piece table (`PieceTree`) based editing with O(log N) positional operations
- Line indexing with SIMD-optimized scanning
- Small, testable core (`Leviathan.Core`) separated from UI

## Projects

- `src/Leviathan.Core` — Headless data engine (core editing, IO, indexing, text decoding)
	- Key subfolders: `DataModel/` (piece table, `PieceTree`), `IO/` (`MappedFileSource`, `AppendBuffer`), `Indexing/` (`LineIndexer`, `LineIndex`), `Search/` (search engine), `Text/` (decoders, encoding utilities).
- `src/Leviathan.UI` — GUI (ImGui + OpenGL render loop)
	- UI has `HexView`, `TextView`, `AppWindow`, `Settings` and platform helpers (eg. `Windows/FindWindow.cs`). Includes `rd.xml` used for Native AOT configuration.
- `src/Leviathan.TUI` — Terminal UI frontend
	- TUI provides `AppState`, `TuiSettings`, ANSI rendering (`Rendering/AnsiBuilder.cs`) and controllers for file/hex/text views.
- `tests/Leviathan.Core.Tests` — xUnit tests for the core library

## Front-ends

There are now three front-ends (GUI, TUI, and an experimental third frontend). Each front-end has its own set of problems and tradeoffs — expect UI-specific, terminal-specific, and experimental issues when testing or contributing.

## Build & test

Build the full solution (Windows / PowerShell):

```powershell
dotnet build Leviathan.slnx
```

Run all tests:

```powershell
dotnet test tests/Leviathan.Core.Tests/Leviathan.Core.Tests.csproj
```

Run a single test class or method with a filter:

```powershell
# single test class
dotnet test tests/Leviathan.Core.Tests/ --filter "FullyQualifiedName~PieceTreeTests"

# single test method
dotnet test tests/Leviathan.Core.Tests/ --filter "FullyQualifiedName~PieceTreeTests.Insert_AtBeginning_ReturnsCorrectContent"
```

Run the UI (optionally pass a file path to open):

```powershell
dotnet run --project src/Leviathan.UI [path-to-file]
```

Publish a Native AOT single-file executable:

```powershell
dotnet publish src/Leviathan.UI/Leviathan.UI.csproj -c Release -r win-x64
```

## Architecture & important conventions

`Document` is the public facade over `MappedFileSource`, `AppendBuffer`, and `PieceTree`.

Core components (high level):

- `MappedFileSource` — memory-mapped backing with zero-copy reads for large files.
- `AppendBuffer` — arena allocator backed by `ArrayPool<byte>` for low-allocation edits.
- `PieceTree` — red-black piece table using value types (`readonly record struct`) for pieces and O(log N) positional ops.
- `LineIndexer` / `LineIndex` — background scanning with a SIMD cascade (AVX2 → SSE2/NEON → scalar) and sparse index checkpoints for fast scroll estimation.
- `SearchEngine` — efficient searching over indexed content; returns `SearchResult` objects used by frontends.
- `Text` subcomponents — encoding detection, multi-encoding decoders (`Utf8TextDecoder`, `Utf16LeTextDecoder`, `Windows1252TextDecoder`), and `Utf8Utils` helpers.

UI & TUI notes:

- UI hot path avoids heap allocations — code uses `stackalloc`, UTF-8 string literals, and raw ImGui/OpenGL draw calls.
- TUI contains `AnsiBuilder` for terminal rendering and controllers for `HexView`/`TextView`.
- `Windows/FindWindow.cs` contains platform helpers when running on Windows.

Performance & AOT:

- The codebase is AOT-aware: avoid runtime reflection and use source-generated JSON contexts for serialization when needed.
- `rd.xml` in `src/Leviathan.UI` contains runtime directives to support Native AOT publishing.

## Code style and testing notes

- Prefer explicit types over `var` in critical code; annotate hot-path methods with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- Public APIs and important internal types should have XML docs.
- Tests are xUnit-based; test naming: `MethodName_Condition_ExpectedResult`.

Additional developer notes:

- Keep hot-path code allocation-free and annotate with `[MethodImpl(MethodImplOptions.AggressiveInlining)]` where appropriate.
- When changing performance-critical code, add micro-benchmarks or tests and run them locally before submitting PRs.
- Use the existing test project to add coverage for core features (`Search`, `PieceTree`, `LineIndex`, decoders, etc.).

## Contribution

Contributions welcome. When opening PRs:

- Keep changes minimal and focused.
- Preserve performance characteristics of hot paths — benchmark before changing critical code.
- Add or update tests in `tests/Leviathan.Core.Tests` for core changes.

## License

See `LICENSE.txt` at the repository root.

