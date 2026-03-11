using System.Text;

namespace Leviathan.Core.Text;

/// <summary>
/// Abstraction over a text encoding for decoding and encoding runes from/to raw byte data.
/// Implementations must be stateless and safe to call from the render hot path.
/// </summary>
public interface ITextDecoder
{
  /// <summary>Which encoding this decoder handles.</summary>
  TextEncoding Encoding { get; }

  /// <summary>
  /// Minimum number of bytes that form a single character unit.
  /// 1 for UTF-8 and Windows-1252, 2 for UTF-16 LE.
  /// </summary>
  int MinCharBytes { get; }

  /// <summary>
  /// Decodes the next rune from <paramref name="data"/> starting at <paramref name="offset"/>.
  /// Returns the decoded <see cref="Rune"/> and how many bytes were consumed.
  /// On invalid sequences, returns <see cref="Rune.ReplacementChar"/> with a best-effort byte length.
  /// </summary>
  (Rune Rune, int ByteLength) DecodeRune(ReadOnlySpan<byte> data, int offset);

  /// <summary>
  /// Adjusts <paramref name="offset"/> backwards so it lands on a valid character boundary.
  /// Returns the adjusted offset (always &lt;= <paramref name="offset"/>).
  /// </summary>
  int AlignToCharBoundary(ReadOnlySpan<byte> data, int offset);

  /// <summary>
  /// Encodes a single rune into <paramref name="output"/> using this encoding.
  /// Returns the number of bytes written. If the rune cannot be represented,
  /// writes a replacement character (U+FFFD for Unicode, 0x3F '?' for single-byte).
  /// </summary>
  int EncodeRune(Rune rune, Span<byte> output);

  /// <summary>
  /// Tests whether the byte sequence at <paramref name="offset"/> represents a newline character
  /// (LF or CR) in this encoding. Returns the number of bytes the newline occupies.
  /// For CR+LF sequences, only the CR is matched here; callers handle the pair.
  /// </summary>
  bool IsNewline(ReadOnlySpan<byte> data, int offset, out int newlineByteLength);

  /// <summary>
  /// Encodes a .NET string into a byte array using this encoding.
  /// Used by the search subsystem to convert query text to a byte pattern.
  /// </summary>
  byte[] EncodeString(string text);
}
