namespace DbClone.UI.Models;

/// <summary>
/// Progress report DTO for the database comparison service.
/// Replaces the simple int percentage with phase-aware progress information.
/// </summary>
public sealed record CompareProgressInfo
{
    public int PercentComplete { get; init; }

    public string CurrentPhase { get; init; } = "";

    public string CurrentTable { get; init; } = "";

    public int TablesProcessed { get; init; }

    public int TotalTables { get; init; }
}
