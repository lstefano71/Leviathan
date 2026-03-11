namespace Leviathan.Core.Text;

/// <summary>
/// Supported text encodings for the text view.
/// </summary>
public enum TextEncoding
{
  /// <summary>UTF-8 (variable-length, 1–4 bytes per character).</summary>
  Utf8,

  /// <summary>UTF-16 Little-Endian (2 or 4 bytes per character).</summary>
  Utf16Le,

  /// <summary>Windows code page 1252 (single-byte Western European).</summary>
  Windows1252,
}
