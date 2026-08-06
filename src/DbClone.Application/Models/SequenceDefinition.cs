namespace DbClone.Application.Models;

/// <summary>
/// Represents a sequence definition.
/// </summary>
/// <param name="SchemaName">Schema that contains the sequence.</param>
/// <param name="Name">Sequence name.</param>
/// <param name="StartValue">First value produced by the sequence.</param>
/// <param name="IncrementBy">Increment step for successive values.</param>
/// <param name="MinValue">Minimum value, or null when unbounded.</param>
/// <param name="MaxValue">Maximum value, or null when unbounded.</param>
/// <param name="CacheSize">Number of sequence numbers cached in memory.</param>
/// <param name="IsCycled">True when the sequence wraps around when reaching its bound.</param>
/// <param name="DataType">Data type of the sequence values.</param>
/// <param name="Comment">Optional comment on the sequence.</param>
/// <param name="OwnerTable">
/// Schema-qualified table that owns this sequence (identity or serial backing sequence).
/// Null for standalone sequences.
/// </param>
/// <param name="OwnerColumn">Column that owns this sequence. Null for standalone sequences.</param>
/// <param name="IsIdentity">
/// True when this sequence is an identity-column backing sequence (pg_depend deptype 'i').
/// Identity sequences are created implicitly by the table DDL and have non-deterministic
/// names across databases. Serial sequences (deptype 'a') are explicit schema objects
/// referenced by name in column defaults and must be created/compared normally.
/// </param>
public sealed record SequenceDefinition(
    string SchemaName,
    string Name,
    long StartValue,
    long IncrementBy,
    long? MinValue,
    long? MaxValue,
    long CacheSize,
    bool IsCycled,
    string? DataType,
    string? Comment,
    string? OwnerTable = null,
    string? OwnerColumn = null,
    bool IsIdentity = false)
{
    /// <summary>True when this sequence backs an identity or serial column.</summary>
    public bool IsOwned => OwnerTable is not null;
}
