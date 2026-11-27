using System.Security.Cryptography;

using CanonicalModel.Interchange;

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
            manifest = await JsonFile.ReadDocumentAsync<ManifestFile>(manifestPath, ct);
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

        // Version and row-format gate
        if (manifest.FormatVersion < InterchangeFormat.Version)
        {
            issues.Add(
                $"This catalog was written by interchange format version {manifest.FormatVersion}, " +
                "which this version cannot read. Re-export the database.");
        }

        if (string.Equals(manifest.Data.Encoding, "ndjson", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(
                "This catalog uses the legacy NDJSON row format, which this version cannot read. " +
                "Re-export the database.");
        }

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
                catalogIndex = await JsonFile.ReadDocumentAsync<CatalogIndex>(catalogIndexPath, ct);
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

                // Verify table count matches (data/<schema>/<table>/ structure)
                var dataDir = Path.Combine(catalogPath, InterchangeFormat.DataDirectory);
                var tableFolderPaths = new List<string>();
                var tableFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(dataDir))
                {
                    foreach (var parentDir in Directory.GetDirectories(dataDir))
                    {
                        var schemaName = Path.GetFileName(parentDir);
                        foreach (var tableDir in Directory.GetDirectories(parentDir))
                        {
                            // Record the relative path (schema/table) for cross-checking
                            var relativePath = $"{schemaName}/{Path.GetFileName(tableDir)}";
                            tableFolderPaths.Add(relativePath);
                            tableFolders.Add(relativePath);
                        }
                    }
                }
                if (tableFolderPaths.Count != catalogIndex.TableCount)
                {
                    issues.Add($"Table count mismatch: catalog index says {catalogIndex.TableCount}, found {tableFolderPaths.Count} table directories.");
                }

                // Tenth integrity check: cross-reference tables section with data folders.
                // Every table in the section has exactly one folder and every folder is claimed by exactly one
                // table — this catches the §2.4 silent data loss where case-colliding names map to one folder.
                //
                // The folder a table should own is the one catalog.json records for it, not one recomputed from
                // its name: a collision-suffixed folder is not re-derivable, and the earlier prefix match let
                // table "images" be satisfied by another table's "images-2" folder while hiding a folder that
                // belonged to nobody. Both halves are now set equality.
                var recordedFolders = new Dictionary<(string Schema, string Table), string>(catalogIndex.TableFolders.Count);
                foreach (var entry in catalogIndex.TableFolders)
                {
                    recordedFolders[(entry.Schema, entry.Table)] = entry.Folder;
                }

                var tablesSectionPath = Path.Combine(schemaDir, "tables.json");
                if (File.Exists(tablesSectionPath) && (tableFolders.Count > 0 || catalogIndex.TableCount > 0))
                {
                    var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        await foreach (var table in JsonFile.ReadObjectsAsync<TableDefinition>(tablesSectionPath, "objects", ct))
                        {
                            var schemaName = table.Ref.SchemaName() ?? "";
                            var folder = recordedFolders.TryGetValue((schemaName, table.Name), out var recorded)
                                ? recorded
                                : $"{NameEncoder.EncodeSegment(schemaName)}/{NameEncoder.EncodeSegment(table.Name)}";

                            claimed.Add(folder);
                            if (!tableFolders.Contains(folder))
                            {
                                issues.Add($"Table '{schemaName}.{table.Name}' is in the catalog but has no data folder.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        issues.Add($"Error reading tables section for integrity check: {ex.Message}");
                    }

                    foreach (var folder in tableFolderPaths)
                    {
                        if (!claimed.Contains(folder))
                        {
                            issues.Add($"Data folder '{folder}' exists but no table in the catalog claims it.");
                        }
                    }
                }

                // The other half of the tenth check: every stored data file of a folder — row chunk or blob
                // container — appears in that folder's checksums.sha256, and nothing sits in the folder that the
                // list does not name. The chain manifest → checksums → files only holds if the list is exactly
                // the set of data files, and the containers left the manifest deliberately (design §8).
                foreach (var folder in tableFolderPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    await VerifyChecksumsMembershipAsync(
                        Path.Combine(dataDir, folder.Replace('/', Path.DirectorySeparatorChar)),
                        folder,
                        issues,
                        ct);
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

    /// <summary>
    /// Compares the data files stored in one table folder with the list its <c>checksums.sha256</c> names.
    /// </summary>
    /// <remarks>
    /// The folder's own two metadata files (<c>table.json</c> and <c>checksums.sha256</c> itself) are excluded:
    /// they are accounted for by <c>manifest.json</c>, which the caller has already checked.
    /// </remarks>
    private static async Task VerifyChecksumsMembershipAsync(
        string tableDir,
        string folder,
        List<string> issues,
        CancellationToken ct)
    {
        var checksumsPath = Path.Combine(tableDir, "checksums.sha256");
        if (!File.Exists(checksumsPath))
        {
            // A folder written before the per-file list existed has nothing to compare against, and the reader
            // still handles it by enumerating the directory — so this stays silent instead of failing a catalog
            // the product can read.
            return;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var line in File.ReadLinesAsync(checksumsPath, ct))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0)
            {
                listed.Add(line[..tab].Replace('\\', '/'));
            }
        }

        var stored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in Directory.EnumerateFiles(tableDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tableDir, filePath).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is "checksums.sha256" or "table.json")
            {
                continue;
            }

            stored.Add(relative);
        }

        foreach (var path in listed)
        {
            if (!stored.Contains(path))
            {
                issues.Add($"File '{folder}/{path}' is listed in checksums.sha256 but is not in the folder.");
            }
        }

        foreach (var path in stored)
        {
            if (!listed.Contains(path))
            {
                issues.Add($"File '{folder}/{path}' is stored in the catalog but is not listed in checksums.sha256.");
            }
        }
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
