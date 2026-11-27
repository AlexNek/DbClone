using CanonicalModel.Interchange;
using DbAdapter.Definitions;

namespace DatabaseInterchange.Format;

/// <summary>
/// Writes each non-NULL binary value to its own out-of-band file.
/// </summary>
/// <remarks>
/// A binary value never enters the CSV row as hex. The row carries a reference to one file containing
/// exactly that value, so extraction and restore do not need to split a shared byte container.
/// </remarks>
internal sealed class BlobContainerWriter : IAsyncDisposable
{
    private readonly string _blobsDir;
    private readonly string _rowFileStem;
    private readonly List<BlobContainerInfo> _files = [];
    private bool _disposed;

    /// <summary>Initializes a writer for one row chunk.</summary>
    public BlobContainerWriter(string tableDir, string rowFileStem)
    {
        _blobsDir = Path.Combine(tableDir, "blobs");
        _rowFileStem = rowFileStem;
    }

    /// <summary>
    /// Writes one binary value to its own file and returns its reference.
    /// </summary>
    /// <param name="hexValue">The source-rendered hexadecimal value, including the <c>\x</c> prefix.</param>
    /// <param name="columnSegment">The encoded, collision-resolved binary column name.</param>
    /// <param name="rowIndex">The zero-based row index within the current chunk.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BlobReference> WriteBlobAsync(
        string hexValue,
        string columnSegment,
        int rowIndex,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bytes = HexToBytes(hexValue);
        var probeLength = Math.Min(bytes.Length, BlobFormat.ProbeBytes);
        var suffix = BlobFormat.DetectSuffix(bytes, probeLength);

        Directory.CreateDirectory(_blobsDir);

        var fileName = $"{_rowFileStem}.{columnSegment}.{rowIndex:D6}.{suffix}";
        var filePath = Path.Combine(_blobsDir, fileName);
        await using (var stream = new FileStream(
                         filePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        _files.Add(new BlobContainerInfo(fileName, bytes.Length, hash));

        return new BlobReference(
            RelativePath: $"{BlobReferenceParser.ContainerDirectory}{fileName}",
            Offset: 0,
            Length: bytes.Length,
            Suffix: suffix);
    }

    /// <summary>Formats a CSV field reference to one external blob file.</summary>
    public static string FormatReference(BlobReference blobRef) =>
        $"@blob:{blobRef.RelativePath}:{blobRef.Offset}:{blobRef.Length}";

    public Task<IReadOnlyList<BlobContainerInfo>> SealAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BlobContainerInfo>>(_files.ToArray());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static byte[] HexToBytes(string hex)
    {
        var start = hex.StartsWith("\\x", StringComparison.Ordinal) ? 2 : 0;
        var hexLen = hex.Length - start;
        if (hexLen % 2 != 0)
        {
            throw new InterchangeFormatException("A binary value contains an odd number of hexadecimal digits.");
        }

        var bytes = new byte[hexLen / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(start + i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        }

        return bytes;
    }
}
