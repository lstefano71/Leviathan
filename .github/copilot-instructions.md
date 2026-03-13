# Copilot Instructions for Leviathan

Leviathan is a native AOT, immediate-mode hex + text editor for files 50 GB+ in size. The hard requirements are **< 500 ms open time** and **zero GC allocations in the render loop**. Every change must be evaluated against both constraints.

---

## Build & Test

```powershell
# Build the full solution
dotnet build Leviathan.slnx

# Run all tests
dotnet test tests/Leviathan.Core.Tests/Leviathan.Core.Tests.csproj

# Run a single test class
dotnet test tests/Leviathan.Core.Tests/ --filter "FullyQualifiedName~PieceTreeTests"

# Run a single test method
dotnet test tests/Leviathan.Core.Tests/ --filter "FullyQualifiedName~PieceTreeTests.Insert_AtBeginning_ReturnsCorrectContent"

# Run the UI (optional file to open)
dotnet run --project src/Leviathan.UI [path-to-file]

# Publish a Native AOT single-file executable
dotnet publish src/Leviathan.UI/Leviathan.UI.csproj -c Release -r win-x64
```

---

## Architecture

The solution has three projects:

| Project | Role |
|---|---|
| `src/Leviathan.Core` | Headless data engine — no UI dependencies |
| `src/Leviathan.UI` | Executable — window, render loop, two view modes |
| `tests/Leviathan.Core.Tests` | xUnit tests for Core only |

### Core data pipeline

`Document` is the public façade over three lower-level components:

- **`MappedFileSource`** (`IO/`) — wraps `MemoryMappedFile`; exposes `unsafe ReadOnlySpan<byte>` via raw pointer arithmetic (zero-copy reads).
- **`AppendBuffer`** (`IO/`) — arena allocator backed by `ArrayPool<byte>`; absorbs all inserted bytes without GC pressure.
- **`PieceTree`** (`DataModel/`) — Red-Black tree piece table with augmented subtree lengths for O(log N) positional lookup, insert, and delete. `Piece` is a `readonly record struct(PieceSource Source, long Offset, long Length)` — value type, zero allocation.

Saves are atomic: write all pieces to `destination.tmp`, then `File.Move(..., overwrite: true)`.

### Indexing

`LineIndexer` (`Indexing/`) runs a background `Task.Run` that feeds 4 MB chunks to `LineIndex.ScanChunk`, which uses a SIMD cascade: AVX2 → SSE2/NEON → scalar. Every 1,000th newline is stored in a sparse `long[]` for O(1) scrollbar estimation over arbitrarily large files.

### UI render loop

`AppWindow` owns the Silk.NET window, OpenGL 3.3 Core context, and ImGui lifecycle. The render callback chain is:

```
AppWindow.Render → _renderCallback(deltaTime) → ImGui.Render() → ImGuiImplOpenGL3.RenderDrawData
```

`HexView` and `TextView` format bytes into `stackalloc byte[]` buffers and write via `ImDrawList.AddText` raw-pointer overloads — no heap allocations in the hot path. Scrollbar position for files larger than 10,000,000 px of virtual height is mapped fractionally using row-index arithmetic.

---

## Key Conventions

### Zero-allocation hot path
- Use `stackalloc` for formatting buffers in render code; never `string.Format` or interpolation.
- Use UTF-8 string literals (`"text"u8`) for all ImGui labels and menu items.
- Annotate hot-path methods with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- `Piece` and `VisualLine` are value types (`readonly record struct` / `readonly struct`) — keep them that way.

### AOT compatibility
- `Leviathan.UI` publishes with `PublishAot = true` and `InvariantGlobalization = true`.
- All JSON serialization must go through a `[JsonSerializable]` / `JsonSerializerContext` source-generated context (see `Settings.cs`). Never use reflection-based `JsonSerializer` overloads.
- Avoid runtime reflection, `dynamic`, and `Activator.CreateInstance`.

### Code style
- `sealed class` everywhere; no inheritance hierarchies — use composition.
- Private fields: `_camelCase`. Public members: `PascalCase`. Constants: `PascalCase` (matching .NET conventions).
- Explicit types over `var`.
- XML doc comments (`/// <summary>`) on all public and significant internal members.
- `IDisposable` with a `_disposed` guard on every class holding OS or unmanaged resources.
- No DI container — constructor injection of concrete types; `Program.cs` (top-level statements) is the composition root.

### Test conventions (xUnit)
- One test class per production class, named `{ClassName}Tests`.
- Method names follow `MethodName_Condition_ExpectedResult`.
- Use `[Fact]`; no `[Theory]` unless parameterisation genuinely adds coverage.
- Mirror the zero-allocation style in tests where practical (`stackalloc` in assertions).
- `CreateTempFile` helper + `try/finally` for any test that needs a real file on disk.

### Performance Optimization
- For performance optimization tasks, run the profiler first to summarize CPU bottlenecks.
- Recommend fixes based on profiling results and baseline/re-run benchmarks for validation.

### Project boundaries
- `Leviathan.Core` must remain UI-free. Never add a reference to `Leviathan.UI` or any GUI package from Core.
- New Core sub-systems belong in a dedicated sub-folder matching the logical layer (`IO/`, `DataModel/`, `Indexing/`, `Text/`).

### IMPORTANT: Documentation for the tools
- documentation for Terminal.Gui2 is here: D:\Trash\Terminal.Gui\docfx\docs
