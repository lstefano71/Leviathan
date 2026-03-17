namespace Leviathan.Core.Csv;

/// <summary>
/// Describes the dialect of a CSV file: which byte values are used as
/// field separator, quote character, and escape character, and whether
/// the first record is a header row.
/// </summary>
/// <remarks>
/// The CSV pipeline operates on single-byte tokens. Separator, quote, and escape
/// are each a single <see cref="byte"/>, so the pipeline is limited to single-byte
/// encodings (UTF-8, ASCII, Windows-1252). Multi-byte encodings such as UTF-16
/// are not currently supported.
/// </remarks>
public readonly record struct CsvDialect(
    byte Separator,
    byte Quote,
    byte Escape,
    bool HasHeader)
{
    /// <summary>Standard RFC 4180 comma-separated dialect.</summary>
    public static CsvDialect Csv(bool hasHeader = true) =>
        new((byte)',', (byte)'"', (byte)'"', hasHeader);

    /// <summary>Tab-separated dialect.</summary>
    public static CsvDialect Tsv(bool hasHeader = true) =>
        new((byte)'\t', (byte)'"', (byte)'"', hasHeader);

    /// <summary>Semicolon-separated dialect (common in European locales).</summary>
    public static CsvDialect Semicolon(bool hasHeader = true) =>
        new((byte)';', (byte)'"', (byte)'"', hasHeader);

    /// <summary>Pipe-separated dialect.</summary>
    public static CsvDialect Pipe(bool hasHeader = true) =>
        new((byte)'|', (byte)'"', (byte)'"', hasHeader);
}
