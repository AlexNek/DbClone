namespace DbAdapter.Definitions;

/// <summary>
/// Snapshot consistency modes.
/// </summary>
public enum ESnapshotConsistency
{
    /// <summary>A consistent snapshot across all tables.</summary>
    ConsistentSnapshot,

    /// <summary>Best-effort; tables may be read at different points in time.</summary>
    BestEffort,

    /// <summary>Schema only; no data.</summary>
    SchemaOnly,
}

/// <summary>
/// Policy for unsupported features during import.
/// </summary>
public enum EUnsupportedFeaturePolicy
{
    /// <summary>Fail the import.</summary>
    Fail,

    /// <summary>Warn but continue.</summary>
    Warn,

    /// <summary>Skip silently.</summary>
    Skip,
}

/// <summary>
/// Policy for suspected secrets in data.
/// </summary>
public enum EFavorSecretPolicy
{
    /// <summary>Export the data (operator's choice).</summary>
    Export,

    /// <summary>Omit suspected secrets.</summary>
    Omit,
}
