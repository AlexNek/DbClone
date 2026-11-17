using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Format;

/// <summary>
/// Turns one stored row's text fields into typed values, applying the reference grammar of design §4 and §6.
/// </summary>
/// <remarks>
/// A reference is recognized only in a column whose recorded logical type is <c>Binary</c>. A non-Binary
/// column may contain ordinary text beginning with <c>@blob:</c>, but a Binary column must contain either NULL
/// or a reference to one external value file.
/// </remarks>
internal sealed class BinaryFieldGrammar : IAsyncDisposable
{
    private readonly BlobContainerReader _containers;

    /// <summary>Initializes the grammar for one table's folder.</summary>
    /// <param name="tableDir">The table directory containing the referenced blob files.</param>
    public BinaryFieldGrammar(string tableDir)
    {
        _containers = new BlobContainerReader(tableDir);
    }

    /// <summary>Decodes one stored row against the table's recorded column types.</summary>
    public async Task<DataValue[]> DecodeRowAsync(
        string?[] fields,
        IReadOnlyList<ELogicalType> columnTypes,
        CancellationToken ct)
    {
        var values = new DataValue[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var logicalType = i < columnTypes.Count ? columnTypes[i] : ELogicalType.Unknown;
            values[i] = await DecodeFieldAsync(fields[i], logicalType, ct);
        }

        return values;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _containers.DisposeAsync();

    private async Task<DataValue> DecodeFieldAsync(
        string? field,
        ELogicalType logicalType,
        CancellationToken ct)
    {
        if (field is null)
        {
            return DataValue.Null(logicalType);
        }

        if (field.StartsWith(BlobReferenceParser.Prefix, StringComparison.Ordinal))
        {
            if (logicalType != ELogicalType.Binary)
            {
                throw new BlobRefInNonBinaryColumnException(
                    $"A \"{BlobReferenceParser.Prefix}\" reference appears in a column of logical type " +
                    $"{logicalType}, which stores text and not a binary value. The field is refused " +
                    $"rather than dereferenced: \"{Truncate(field)}\".");
            }

            if (!BlobReferenceParser.TryParse(field, out var reference) || reference is null)
            {
                throw new MalformedBinaryTokenException(
                    $"Malformed blob reference field: \"{Truncate(field)}\".");
            }

            var blobPath = await _containers.VerifyAsync(reference, ct);
            return DataValue.FromPayload(
                logicalType,
                new BlobPayload(blobPath, reference.Offset, reference.Length));
        }

        if (logicalType == ELogicalType.Binary)
        {
            throw new MalformedBinaryTokenException(
                $"A Binary field is neither NULL nor a blob reference: \"{Truncate(field)}\".");
        }

        return DataValue.FromText(logicalType, field);
    }

    private static string Truncate(string value, int max = 60) =>
        value.Length <= max ? value : value[..max] + "…";
}
