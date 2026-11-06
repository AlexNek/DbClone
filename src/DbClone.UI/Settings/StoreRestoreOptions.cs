namespace DbClone.UI.Settings;

/// <summary>
/// Store / Restore workspace options.
/// Plain POCO — assigned as a whole object on <see cref="UserSettings.StoreRestore"/>
/// so the existing top-level PropertyChanged persistence picks it up.
/// </summary>
public sealed class StoreRestoreOptions
{
    /// <summary>Default directory for new catalogs (user-visible starting point).</summary>
    public string DefaultCatalogDirectory { get; set; } =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DbClone Catalogs");

    /// <summary>
    /// Subfolder naming pattern under <see cref="DefaultCatalogDirectory"/>.
    /// Supported placeholders: <c>{Database}</c> (source database name),
    /// <c>{timestamp}</c> (YYYY-MM-DD_HH-mm). The folder is auto-created on Store.
    /// </summary>
    public string CatalogSubfolderPattern { get; set; } = "{Database}_{timestamp}";

    /// <summary>Target chunk size in bytes (default 8 MiB).</summary>
    public int ChunkBytes { get; set; } = 8_388_608;

    /// <summary>Maximum rows per data chunk (default 50 000).</summary>
    public int RowsPerChunk { get; set; } = 50_000;

    /// <summary>Whether to include table data in the catalog (not just schema).</summary>
    public bool IncludeData { get; set; } = true;

    /// <summary>Snapshot consistency mode passed to the product (e.g. "consistent").</summary>
    public string SnapshotConsistency { get; set; } = "consistent";

    /// <summary>
    /// What to do when the catalog contains objects the product cannot handle:
    /// "fail" (default, refuse) or "warn" (skip with an issue).
    /// </summary>
    public string UnsupportedFeaturePolicy { get; set; } = "fail";

    /// <summary>Whether to pack the catalog into a .rdbx.zip after a successful store.</summary>
    public bool PackAfterStore { get; set; } = true;
}
