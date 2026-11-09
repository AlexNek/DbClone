using System.Text;

namespace DatabaseInterchange.Format;

/// <summary>
/// Encodes and decodes a single CSV field per the v2 catalog contract (RFC 4180 with the
/// <c>\N</c> NULL convention).
/// </summary>
/// <remarks>
/// <para>Write rule: quote iff the value is empty, equals <c>\N</c>, begins or ends with space or
/// tab, or contains <c>"</c>, <c>,</c>, CR or LF.</para>
/// <para>Read rule: a quoted field is literal with <c>""</c> → <c>"</c>; an unquoted field is
/// literal except the bare <c>\N</c> → NULL; <c>""</c> (quoted empty) → empty string.</para>
/// <para>No other escape language exists. A value that is literally the two characters <c>\N</c>
/// is written <c>"\N"</c> (quoted), so it round-trips as data, not NULL.</para>
/// </remarks>
public static class CsvField
{
    /// <summary>The two-character token that represents NULL in an unquoted CSV field.</summary>
    public const string NullToken = @"\N";

    /// <summary>
    /// Encodes a single value as a CSV field. Returns the encoded field text (without the
    /// inter-field comma).
    /// </summary>
    /// <param name="value">
    /// The value to encode. <see langword="null"/> represents a SQL NULL.
    /// </param>
    /// <returns>The CSV field text.</returns>
    public static string Encode(string? value)
    {
        if (value is null) return NullToken;

        if (NeedsQuoting(value))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    /// <summary>
    /// Decodes a single CSV field text back to a value. Returns <see langword="null"/> for a SQL
    /// NULL (bare <c>\N</c>), or the literal text for all other fields.
    /// </summary>
    /// <param name="field">The raw field text (without surrounding commas or quotes).</param>
    /// <returns>The decoded value, or <see langword="null"/> for NULL.</returns>
    public static string? Decode(string field)
    {
        if (field.Length == 0) return string.Empty;

        if (field[0] == '"')
        {
            // Quoted field: strip outer quotes, un-double inner quotes. Literal content.
            var inner = field[1..^1];
            return inner.Replace("\"\"", "\"");
        }

        // Unquoted field: bare \N is NULL; everything else is literal.
        if (field == NullToken) return null;

        return field;
    }

    /// <summary>
    /// Parses one complete CSV record from the input, handling quoted fields that contain commas,
    /// newlines, and escaped quotes. Returns the list of decoded field values and the number of
    /// characters consumed from the input.
    /// </summary>
    /// <param name="input">The input text starting at the beginning of a record.</param>
    /// <returns>The decoded field values and the number of characters consumed.</returns>
    public static (List<string?> Fields, int Consumed) ParseRecord(string input)
    {
        var fields = new List<string?>();
        var pos = 0;

        while (pos <= input.Length)
        {
            if (pos == input.Length)
            {
                // End of input: emit final empty field only if we just saw a comma.
                // This is handled by the caller knowing the field count.
                break;
            }

            if (input[pos] == '"')
            {
                // Quoted field.
                var sb = new StringBuilder();
                pos++; // skip opening quote

                while (pos < input.Length)
                {
                    if (input[pos] == '"')
                    {
                        if (pos + 1 < input.Length && input[pos + 1] == '"')
                        {
                            sb.Append('"');
                            pos += 2;
                        }
                        else
                        {
                            pos++; // skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(input[pos]);
                        pos++;
                    }
                }

                fields.Add(sb.ToString());

                // After closing quote: expect comma or end-of-record (CR/LF/EOI).
                if (pos < input.Length && input[pos] == ',')
                {
                    pos++; // skip comma, next field
                }
                else
                {
                    // End of record — skip the line ending.
                    pos = SkipLineEnding(input, pos);
                    break;
                }
            }
            else
            {
                // Unquoted field — read until comma or line ending.
                var start = pos;
                while (pos < input.Length && input[pos] != ',' && input[pos] != '\r' && input[pos] != '\n')
                {
                    pos++;
                }

                var raw = input[start..pos];
                fields.Add(Decode(raw));

                if (pos < input.Length && input[pos] == ',')
                {
                    pos++; // skip comma
                }
                else
                {
                    // End of record.
                    pos = SkipLineEnding(input, pos);
                    break;
                }
            }
        }

        return (fields, pos);
    }

    /// <summary>
    /// Writes a complete CSV record (one line) from a list of values, using the encoding rules.
    /// The record is terminated with LF.
    /// </summary>
    /// <param name="sb">The string builder to append to.</param>
    /// <param name="values">The field values (null = NULL).</param>
    public static void WriteRecord(StringBuilder sb, IReadOnlyList<string?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Encode(values[i]));
        }

        sb.Append('\n');
    }

    /// <summary>
    /// Encodes a single value and writes it directly to a <see cref="TextWriter"/>, avoiding
    /// the intermediate <see cref="string"/> allocation that <see cref="Encode"/> requires.
    /// </summary>
    /// <param name="writer">The text writer to write to.</param>
    /// <param name="value">
    /// The value to encode. <see langword="null"/> represents a SQL NULL (writes <c>\N</c>).
    /// </param>
    public static void WriteTo(TextWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(NullToken);
            return;
        }

        if (NeedsQuoting(value))
        {
            writer.Write('"');
            foreach (var c in value)
            {
                if (c == '"')
                {
                    writer.Write('"');
                    writer.Write('"');
                }
                else
                {
                    writer.Write(c);
                }
            }
            writer.Write('"');
        }
        else
        {
            writer.Write(value);
        }
    }

    /// <summary>
    /// Writes a complete CSV record (one line) directly to a <see cref="TextWriter"/>, using
    /// the encoding rules. The record is terminated with LF.
    /// </summary>
    /// <param name="writer">The text writer to write to.</param>
    /// <param name="values">The field values (null = NULL).</param>
    public static void WriteRecordTo(TextWriter writer, IReadOnlyList<string?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) writer.Write(',');
            WriteTo(writer, values[i]);
        }

        writer.Write('\n');
    }

    /// <summary>
    /// Returns whether the value must be wrapped in double quotes per the CSV contract.
    /// </summary>
    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0) return true;
        if (value == NullToken) return true;
        if (value[0] == ' ' || value[0] == '\t') return true;
        if (value[^1] == ' ' || value[^1] == '\t') return true;

        foreach (var c in value)
        {
            if (c == '"' || c == ',' || c == '\r' || c == '\n'
                || (c < ' ' && c != '\t'))
            {
                return true;
            }
        }

        return false;
    }

    private static int SkipLineEnding(string input, int pos)
    {
        if (pos >= input.Length) return pos;
        if (input[pos] == '\r')
        {
            pos++;
            if (pos < input.Length && input[pos] == '\n') pos++;
            return pos;
        }

        if (input[pos] == '\n') return pos + 1;

        return pos;
    }
}
