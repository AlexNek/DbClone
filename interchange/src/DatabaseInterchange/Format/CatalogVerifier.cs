using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DatabaseInterchange.Format;

/// <summary>
/// Verifies the integrity of a catalog directory.
/// </summary>
public sealed class CatalogVerifier
{
    /// <summary>
    /// Verifies a catalog's integrity by checking file presence, hashes, and counts.
    /// </summary>
    /// <param name="catalogPath">The catalog directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A verification result.</returns>
    public async Task<CatalogVerificationResult> VerifyAsync(string catalogPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        if (!Directory.Exists(catalogPath))
        {
            return new CatalogVerificationResult
            {
                IsValid = false,
                Issues = [$"Catalog directory not found: {catalogPath}"],
            };
        }

        var issues = new List<string>();

        // Check manifest exists
        var manifestPath = Path.Combine(catalogPath, InterchangeFormat.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            issues.Add($"Missing manifest file: {InterchangeFormat.ManifestFileName}");
            return new CatalogVerificationResult { IsValid = false, Issues = issues };
        }

        // Load manifest
        ManifestFile? manifest;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, ct);
            manifest = JsonSerializer.Deserialize<ManifestFile>(json, CatalogJson.ReaderOptions);
            if (manifest is null)
            {
                issues.Add("Failed to deserialize manifest.");
                return new CatalogVerificationResult { IsValid = false, Issues = issues };
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Error reading manifest: {ex.Message}");
            return new CatalogVerificationResult { IsValid = false, Issues = issues };
        }

        // Version gate
        if (manifest.FormatVersion > InterchangeFormat.Version)
        {
            issues.Add($"Catalog format version {manifest.FormatVersion} is newer than supported version {InterchangeFormat.Version}.");
        }

        if (manifest.MinimumReader > InterchangeFormat.MinimumReader)
        {
            issues.Add($"Catalog requires minimum reader version {manifest.MinimumReader}, but this reader is version {InterchangeFormat.MinimumReader}.");
        }

        // Check catalog.json exists
        var catalogIndexPath = Path.Combine(catalogPath, InterchangeFormat.CatalogFileName);
        if (!File.Exists(catalogIndexPath))
        {
            issues.Add($"Missing catalog index file: {InterchangeFormat.CatalogFileName}");
        }

        // Verify each file in the manifest
        foreach (var fileEntry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var filePath = Path.Combine(catalogPath, fileEntry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                issues.Add($"Missing file: {fileEntry.Path}");
                continue;
            }

            // Verify hash
            var actualHash = await ComputeSha256Async(filePath, ct);
            if (!string.Equals(actualHash, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Hash mismatch for {fileEntry.Path}: expected {fileEntry.Sha256}, got {actualHash}");
            }

            // Verify size
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length != fileEntry.Bytes)
            {
                issues.Add($"Size mismatch for {fileEntry.Path}: expected {fileEntry.Bytes}, got {fileInfo.Length}");
            }
        }

        // Verify section files exist
        var schemaDir = Path.Combine(catalogPath, InterchangeFormat.SchemaDirectory);
        if (Directory.Exists(schemaDir))
        {
            CatalogIndex? catalogIndex;
            try
            {
                var indexJson = await File.ReadAllTextAsync(catalogIndexPath, Encoding.UTF8, ct);
                catalogIndex = JsonSerializer.Deserialize<CatalogIndex>(indexJson, CatalogJson.ReaderOptions);
            }
            catch
            {
                catalogIndex = null;
            }

            if (catalogIndex is not null)
            {
                foreach (var section in catalogIndex.Sections)
                {
                    var sectionPath = Path.Combine(schemaDir, $"{section}.json");
                    if (!File.Exists(sectionPath))
                    {
                        issues.Add($"Section file missing: {section}.json");
                    }
                }

                // Verify table count matches
                var dataDir = Path.Combine(catalogPath, InterchangeFormat.DataDirectory);
                var actualTableCount = Directory.Exists(dataDir) ? Directory.GetDirectories(dataDir).Length : 0;
                if (actualTableCount != catalogIndex.TableCount)
                {
                    issues.Add($"Table count mismatch: catalog index says {catalogIndex.TableCount}, found {actualTableCount} table directories.");
                }
            }
        }

        await Task.CompletedTask;

        return new CatalogVerificationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
        };
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>
/// Result of catalog verification.
/// </summary>
public sealed record CatalogVerificationResult
{
    /// <summary>Gets whether the catalog is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Gets the verification issues.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}
