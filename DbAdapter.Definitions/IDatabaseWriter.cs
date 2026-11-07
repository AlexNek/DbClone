using CanonicalModel.Interchange;

namespace DbAdapter.Definitions;

/// <summary>
/// The writer role: applies a snapshot to the target database.
/// Created per import operation; owns its connection and transaction.
/// </summary>
public interface IDatabaseWriter : IAsyncDisposable
{
    /// <summary>Gets the adapter that created this writer.</summary>
    IDatabaseAdapter Adapter { get; }

    /// <summary>Gets the per-target capability detector over this writer's connection.</summary>
    ICapabilityDetector CapabilityDetector { get; }

    /// <summary>
    /// Ensures the target database is empty before import begins.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the check passes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the target is not empty.</exception>
    Task EnsureEmptyTargetAsync(CancellationToken ct);

    /// <summary>
    /// Applies schema objects in the specified stage.
    /// </summary>
    /// <param name="stage">The schema stage.</param>
    /// <param name="objects">The objects to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplySchemaAsync(SchemaStage stage, IReadOnlyList<ISchemaObject> objects, CancellationToken ct);

    /// <summary>
    /// Writes rows to a table.
    /// </summary>
    /// <param name="table">The table to write to.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteTableRowsAsync(TableRef table, IAsyncEnumerable<DataValue[]> rows, CancellationToken ct);

    /// <summary>
    /// Applies security objects in the specified stage.
    /// </summary>
    /// <param name="stage">The security stage.</param>
    /// <param name="security">The security section to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplySecurityAsync(ESecurityStage stage, SecuritySection security, CancellationToken ct);

    /// <summary>
    /// Validates the target against expectations after import.
    /// </summary>
    /// <param name="expectation">The expected target state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validation report.</returns>
    Task<ImportValidationReport> ValidateAsync(TargetExpectation expectation, CancellationToken ct);
}

/// <summary>
/// Expected target state for post-import validation.
/// </summary>
public sealed record TargetExpectation
{
    /// <summary>Gets or sets the expected object counts by kind.</summary>
    public IReadOnlyDictionary<EObjectKind, int> ObjectCounts { get; init; } =
        new Dictionary<EObjectKind, int>();

    /// <summary>Gets or sets the expected table row counts.</summary>
    public IReadOnlyDictionary<TableRef, long> TableRowCounts { get; init; } =
        new Dictionary<TableRef, long>();

    /// <summary>Gets or sets the expected sequence states.</summary>
    public IReadOnlyDictionary<InterchangeId, SequenceState> SequenceStates { get; init; } =
        new Dictionary<InterchangeId, SequenceState>();
}

/// <summary>
/// Expected state of a sequence after import.
/// </summary>
public sealed record SequenceState
{
    /// <summary>Gets or sets the current value.</summary>
    public long CurrentValue { get; init; }

    /// <summary>Gets or sets whether the sequence has been called.</summary>
    public bool IsCalled { get; init; }
}

/// <summary>
/// Report from post-import validation.
/// </summary>
public sealed record ImportValidationReport
{
    /// <summary>Gets or sets whether validation passed.</summary>
    public bool Passed { get; init; }

    /// <summary>Gets or sets the validation issues.</summary>
    public IReadOnlyList<InterchangeIssue> Issues { get; init; } = [];

    /// <summary>Gets or sets the object counts found.</summary>
    public IReadOnlyDictionary<EObjectKind, int> FoundObjectCounts { get; init; } =
        new Dictionary<EObjectKind, int>();

    /// <summary>Gets or sets the row counts found.</summary>
    public IReadOnlyDictionary<TableRef, long> FoundRowCounts { get; init; } =
        new Dictionary<TableRef, long>();
}
