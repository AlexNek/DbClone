namespace DbClone.UI.Settings;

/// <summary>
/// Store / Restore workspace options.
/// Plain POCO — assigned as a whole object on <see cref="UserSettings.StoreRestore"/>
/// so the existing top-level PropertyChanged persistence picks it up. It holds no I/O: the
/// catalog folder is created by the workflow that writes into it
/// (<c>IWorkflowValidator.EnsureDirectoryExists</c>), which owns the failure path for a
/// directory the user cannot write.
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
    /// Supported placeholders: <c>{Connection}</c> (saved connection name, or the source database
    /// name when no saved connection is selected), <c>{Database}</c> (source database name),
    /// <c>{timestamp}</c> (YYYY-MM-DD_HH-mm). The folder is auto-created on Store.
    /// </summary>
    public string CatalogSubfolderPattern { get; set; } = "{Connection}_{timestamp}";

    /// <summary>Target chunk size in bytes (default 8 MiB).</summary>
    public int ChunkBytes { get; set; } = 8_388_608;

    /// <summary>Maximum rows per data chunk (default 50 000).</summary>
    public int RowsPerChunk { get; set; } = 50_000;

    /// <summary>Whether to include table data in the catalog (not just schema).</summary>
    public bool IncludeData { get; set; } = true;

    /// <summary>Whether the store writes the human-readable summary file into the catalog folder.</summary>
    public bool WriteSummaryMarkdown { get; set; } = true;

    /// <summary>
    /// Whether the store also writes the catalog's SQL scripts beside it. On by default: the files are
    /// the reason a catalog can be handed to any client, and a user who never opens the settings should
    /// still get them.
    /// </summary>
    public bool SqlScripts { get; set; } = true;

    /// <summary>
    /// The largest a data script file may become, in megabytes; <c>0</c> writes one file whatever the size.
    /// On by default at 32 MB: a beginner who never opens the settings still gets files a client, an
    /// editor and a chat window can open. The product never sees this unit — the application converts it
    /// once (D11).
    /// </summary>
    public int SqlScriptsMaxFileMb { get; set; } = 32;

    /// <summary>Snapshot consistency mode passed to the product (e.g. "consistent").</summary>
    public string SnapshotConsistency { get; set; } = "consistent";

    /// <summary>
    /// What to do when the catalog contains objects the product cannot handle:
    /// "fail" (default, refuse) or "warn" (skip with an issue).
    /// </summary>
    public string UnsupportedFeaturePolicy { get; set; } = "fail";

}
