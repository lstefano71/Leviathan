using System.Runtime.CompilerServices;

namespace Leviathan.Core.Csv;

/// <summary>
/// Represents a single parsed field within a CSV record.
/// </summary>
public readonly record struct CsvField(int Offset, int Length, bool IsQuoted);

/// <summary>
/// Zero-allocation CSV field parser. Given a byte span containing a single record
/// (without the terminating newline), splits it into field boundaries described as
/// <see cref="CsvField"/> values written into a caller-provided span.
/// </summary>
/// <remarks>
/// Field boundaries are detected using single-byte comparisons. This limits the
/// parser to single-byte encodings (UTF-8, ASCII, Windows-1252). UTF-16 and other
/// multi-byte encodings are not supported.
/// </remarks>
public static class CsvFieldParser
{
    /// <summary>
    /// Parses a single CSV record into fields.
    /// </summary>
    /// <param name="record">
    /// The raw bytes of one record, <b>excluding</b> the terminating line ending.
    /// </param>
    /// <param name="dialect">The CSV dialect to use.</param>
    /// <param name="fields">
    /// Caller-provided buffer for the output fields. If the record contains more fields
    /// than the buffer can hold, parsing stops and the return value equals <c>fields.Length</c>.
    /// </param>
    /// <returns>The number of fields written to <paramref name="fields"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseRecord(ReadOnlySpan<byte> record, CsvDialect dialect, Span<CsvField> fields)
    {
        if (fields.Length == 0)
            return 0;

        byte sep = dialect.Separator;
        byte quote = dialect.Quote;
        byte escape = dialect.Escape;
        int fieldIndex = 0;
        int pos = 0;

        while (pos <= record.Length && fieldIndex < fields.Length) {
            if (pos == record.Length) {
                // Trailing separator produces an empty final field
                // only when the last byte was a separator. But we also reach here
                // at end-of-record normally. If record is empty or previous char was sep,
                // we should emit an empty field. We handle this after the loop exits
                // via the fieldIndex == 0 check for empty records. Otherwise the last
                // field is already captured below.
                break;
            }

            bool isQuoted = record[pos] == quote && quote != 0;

            if (isQuoted) {
                int fieldStart = pos;
                pos++; // skip opening quote

                // Scan to the matching close quote, handling escaped quotes
                while (pos < record.Length) {
                    if (record[pos] == escape && escape == quote) {
                        // RFC 4180: doubled quote
                        if (pos + 1 < record.Length && record[pos + 1] == quote) {
                            pos += 2; // skip escaped quote pair
                            continue;
                        }
                        // Closing quote
                        break;
                    } else if (record[pos] == escape && pos + 1 < record.Length && record[pos + 1] == quote) {
                        // Backslash-style escape: skip escape + quote
                        pos += 2;
                        continue;
                    } else if (record[pos] == quote) {
                        // Closing quote (when escape != quote)
                        break;
                    }
                    pos++;
                }

                // pos now points at closing quote (or end of record if malformed)
                int fieldEnd = pos;
                if (pos < record.Length)
                    pos++; // skip closing quote

                // Skip separator (or trailing whitespace until separator)
                if (pos < record.Length && record[pos] == sep)
                    pos++;

                fields[fieldIndex++] = new CsvField(fieldStart, fieldEnd - fieldStart + (fieldEnd < record.Length ? 1 : 0), true);
            } else {
                int fieldStart = pos;
                while (pos < record.Length && record[pos] != sep)
                    pos++;

                fields[fieldIndex++] = new CsvField(fieldStart, pos - fieldStart, false);

                if (pos < record.Length)
                    pos++; // skip separator
            }
        }

        // Handle empty record → at least one empty field
        if (fieldIndex == 0 && record.Length == 0) {
            fields[0] = new CsvField(0, 0, false);
            return 1;
        }

        // Trailing separator → extra empty field
        if (record.Length > 0 && record[^1] == sep && fieldIndex < fields.Length) {
            fields[fieldIndex++] = new CsvField(record.Length, 0, false);
        }

        return fieldIndex;
    }

    /// <summary>
    /// Extracts the unescaped content of a single field into a destination span.
    /// For quoted fields, the outer quotes are stripped and doubled quotes are unescaped.
    /// </summary>
    /// <param name="record">The full record bytes.</param>
    /// <param name="field">The field descriptor returned by <see cref="ParseRecord"/>.</param>
    /// <param name="dialect">The CSV dialect.</param>
    /// <param name="destination">Buffer to receive the unescaped content.</param>
    /// <returns>
    /// Number of bytes written to <paramref name="destination"/>.
    /// If the destination is smaller than the unescaped field, the output is truncated.
    /// </returns>
    public static int UnescapeField(ReadOnlySpan<byte> record, CsvField field, CsvDialect dialect, Span<byte> destination)
    {
        ReadOnlySpan<byte> raw = record.Slice(field.Offset, field.Length);

        if (!field.IsQuoted) {
            int toCopy = Math.Min(raw.Length, destination.Length);
            raw[..toCopy].CopyTo(destination);
            return toCopy;
        }

        byte quote = dialect.Quote;
        byte escape = dialect.Escape;

        // Strip outer quotes
        if (raw.Length >= 2 && raw[0] == quote && raw[^1] == quote)
            raw = raw[1..^1];
        else if (raw.Length >= 1 && raw[0] == quote)
            raw = raw[1..];

        int written = 0;

        for (int i = 0; i < raw.Length; i++) {
            if (written >= destination.Length)
                break;

            if (raw[i] == escape && i + 1 < raw.Length && raw[i + 1] == quote) {
                destination[written++] = quote;
                i++; // skip the next char
            } else {
                destination[written++] = raw[i];
            }
        }

        return written;
    }
}
