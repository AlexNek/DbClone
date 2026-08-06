using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using DbClone.Application.Enums;
using DbClone.UI.Models;

namespace DbClone.UI.Settings;

/// <summary>
/// Observable user-settings model. Every bound setting raises PropertyChanged,
/// allowing the persistence manager to save on any change without filtering.
/// Persistence I/O is handled by <see cref="Services.SettingsService"/>.
/// </summary>
public sealed partial class UserSettings : ObservableObject
{
    // ── Observable settings (bound by UI, trigger persistence) ─────────────────

    [ObservableProperty]
    private bool _copyData = true;

    [ObservableProperty]
    private bool _copyFunctions = true;

    [ObservableProperty]
    private bool _copyIndexes = true;

    [ObservableProperty]
    private bool _comparePlatformSchemas = true;

    [ObservableProperty]
    private bool _copyPlatformSchemas = true;

    [ObservableProperty]
    private bool _copyTriggers = true;

    [ObservableProperty]
    private bool _copyViews = true;

    /// <summary>Gets or sets the last selected connection group ID.</summary>
    [ObservableProperty]
    private string? _selectedConnectionGroupId;

    [ObservableProperty]
    private ECopyMode _selectedCopyMode = ECopyMode.Full;

    [ObservableProperty]
    private EWorkspaceMode _selectedWorkspaceMode = EWorkspaceMode.Copy;

    [ObservableProperty]
    private EVerifyMode _selectedVerifyMode = EVerifyMode.RowCount;

    [ObservableProperty]
    private EThemeMode _theme = EThemeMode.System;

    // ── Computed properties ────────────────────────────────────────────────────

    /// <summary>Summary of which DB objects are selected for copy.</summary>
    public string CopyObjectsSummary
    {
        get
        {
            var parts = new List<string>(5);
            if (CopyData) parts.Add("Data");
            if (CopyIndexes) parts.Add("Indexes");
            if (CopyViews) parts.Add("Views");
            if (CopyFunctions) parts.Add("Functions");
            if (CopyTriggers) parts.Add("Triggers");
            return parts.Count == 5 ? "All" : parts.Count == 0 ? "None" : string.Join(", ", parts);
        }
    }

    /// <summary>Gets or sets the default format Id for quick clipboard export (e.g. "pg-npgsql").</summary>
    public string DefaultClipboardFormatId { get; set; } = "pg-npgsql";

    /// <summary>Gets or sets the saved destination connection.</summary>
    public ConnectionSettings? Destination { get; set; }

    /// <summary>Gets or sets whether the log pane is expanded in Compare mode (persisted as the legacy key).</summary>
    [JsonPropertyName("IsCompareLogExpanded")]
    public bool CompareLogPaneExpanded { get; set; }

    /// <summary>Gets or sets the log pane height in pixels when expanded in Compare mode.</summary>
    public double CompareLogPaneHeight { get; set; } = 200;

    /// <summary>Gets or sets whether the log pane is expanded in Copy mode.</summary>
    public bool CopyLogPaneExpanded { get; set; }

    /// <summary>Gets or sets the log pane height in pixels when expanded in Copy mode (persisted as the legacy key).</summary>
    [JsonPropertyName("LogPaneHeight")]
    public double CopyLogPaneHeight { get; set; } = 200;

    /// <summary>Summary of platform schema inclusion for the compare info bar.</summary>
    public string ComparePlatformSchemasSummary => ComparePlatformSchemas ? "Included" : "Excluded";

    /// <summary>Summary of platform schema inclusion for the copy info bar.</summary>
    public string CopyPlatformSchemasSummary => CopyPlatformSchemas ? "Included" : "Excluded";

    /// <summary>Gets or sets the schema panel expanded width.</summary>
    public double SchemaPanelExpandedWidth { get; set; } = 200;

    // ── Non-observable properties (read/written at startup/shutdown only) ──────

    /// <summary>Gets or sets the saved source connection.</summary>
    public ConnectionSettings? Source { get; set; }

    /// <summary>Limitation hint for the selected verify mode.</summary>
    public string VerifyModeLimitation =>
        SelectedVerifyMode switch
            {
                EVerifyMode.RowCount =>
                    "RowCount mode cannot detect modified rows with the same count.",
                EVerifyMode.Checksum =>
                    "Checksum mode detects content differences but does not show row-level details.",
                EVerifyMode.Full =>
                    "Full mode compares every row and may be slow on large databases.",
                _ => string.Empty
            };

    /// <summary>Gets or sets the saved window height.</summary>
    public double WindowHeight { get; set; } = double.NaN;

    /// <summary>Gets or sets the saved window left position.</summary>
    public double WindowLeft { get; set; } = double.NaN;

    /// <summary>Gets or sets whether the window was maximized.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>Gets or sets the saved window top position.</summary>
    public double WindowTop { get; set; } = double.NaN;

    /// <summary>Gets or sets the saved window width.</summary>
    public double WindowWidth { get; set; } = double.NaN;

    // ── Re-raise computed property changes ─────────────────────────────────────

    partial void OnCopyDataChanged(bool value) => OnPropertyChanged(nameof(CopyObjectsSummary));

    partial void OnCopyFunctionsChanged(bool value) =>
        OnPropertyChanged(nameof(CopyObjectsSummary));

    partial void OnCopyIndexesChanged(bool value) => OnPropertyChanged(nameof(CopyObjectsSummary));

    partial void OnComparePlatformSchemasChanged(bool value) =>
        OnPropertyChanged(nameof(ComparePlatformSchemasSummary));

    partial void OnCopyPlatformSchemasChanged(bool value) =>
        OnPropertyChanged(nameof(CopyPlatformSchemasSummary));

    partial void OnCopyTriggersChanged(bool value) => OnPropertyChanged(nameof(CopyObjectsSummary));

    partial void OnCopyViewsChanged(bool value) => OnPropertyChanged(nameof(CopyObjectsSummary));

    partial void OnSelectedVerifyModeChanged(EVerifyMode value) =>
        OnPropertyChanged(nameof(VerifyModeLimitation));
}

/// <summary>
/// Saved connection fields (identity only — passwords live in the DPAPI-encrypted ConnectionStore).
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>Gets or sets the database name.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Gets or sets the server host.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Gets or sets the stable connection ID (matches <see cref="Models.SavedConnection.Id"/>).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the port number.</summary>
    public string Port { get; set; } = "5432";

    /// <summary>Gets or sets the username.</summary>
    public string Username { get; set; } = "postgres";
}
