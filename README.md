# Leviathan

Leviathan is a native-AOT, immediate-mode hex + text editor designed for extremely large files (50 GB+). It focuses on minimal allocations and low-latency rendering so the UI and core can operate within strict performance constraints (sub-500 ms open time and zero GC allocations in the render loop).

## Key features

- Extremely large file support (50 GB+)
- Zero-allocation hot paths in the render loop
- Memory-mapped file backing and append buffer for efficient editing
- Piece table (`PieceTree`) based editing with O(log N) positional operations
- Line indexing with SIMD-optimized scanning
- Small, testable core (`Leviathan.Core`) separated from UI

## Projects

- `src/Leviathan.Core` — Headless data engine (core editing, IO, indexing, text decoding)
- `src/Leviathan.UI` — GUI (ImGui + OpenGL render loop)
- `src/Leviathan.TUI` — Terminal UI frontend
- `tests/Leviathan.Core.Tests` — xUnit tests for the core library

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

- `Document` is the public facade over `MappedFileSource`, `AppendBuffer`, and `PieceTree`.
- `MappedFileSource` exposes zero-copy reads via memory-mapped files.
- `AppendBuffer` is an arena backed by `ArrayPool<byte>` to avoid GC pressure on inserts.
- `PieceTree` is a red-black piece table using value types (`readonly record struct`) for pieces.
- `LineIndexer` scans file data in background tasks and uses SIMD (AVX2/SSE2/NEON) cascades.
- UI render hot path avoids heap allocations (use `stackalloc`, raw pointer APIs, UTF-8 literals).
- AOT-friendly: avoid runtime reflection; use source-generated JSON contexts for serialization.

## Code style and testing notes

- Prefer explicit types over `var` in critical code; annotate hot-path methods with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- Public APIs and important internal types should have XML docs.
- Tests are xUnit-based; test naming: `MethodName_Condition_ExpectedResult`.

## Contribution

Contributions welcome. When opening PRs:

- Keep changes minimal and focused.
- Preserve performance characteristics of hot paths — benchmark before changing critical code.
- Add or update tests in `tests/Leviathan.Core.Tests` for core changes.

## License

See `LICENSE.txt` at the repository root.

