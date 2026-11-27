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
    /// <param name="stage">The schema stage. Each stage is handed only the objects the format routes to
    /// it — for the objects that arrive through the catalog's native file, see <see
    /// cref="Interchange.NativeObjectRouting"/> — so a stage creates its own kinds and never has to
    /// decide what to do with an object that belongs to another.</param>
    /// <param name="objects">The objects to apply. A child the format stores inside its parent — a
    /// foreign key, an index — may arrive as the parent that owns it instead of as a list of the
    /// children, because what to do with one is decided against the siblings recorded in the same
    /// object; an adapter walks the objects it is handed and creates what each of them holds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when an object's parent cannot be resolved
    /// from its qualified-name reference, because creating it in a guessed schema is not a failure
    /// this product is allowed to risk.</exception>
    Task ApplySchemaAsync(
        SchemaStage stage,
        IReadOnlyList<ISchemaObject> objects,
        CancellationToken ct);

    /// <summary>
    /// Writes rows to a table.
    /// </summary>
    /// <param name="table">The table to write to.</param>
    /// <param name="columns">The columns the catalog recorded for the table. A stored row is a list of
    /// text tokens with no type attached to any of them — the tokens answer the column order the
    /// catalog holds — so the writer cannot place a value without being told which column each one
    /// belongs to and what type to read it as.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows applied.</returns>
    Task<long> WriteTableRowsAsync(
        TableRef table,
        IReadOnlyList<ColumnDefinition> columns,
        IAsyncEnumerable<DataValue[]> rows,
        CancellationToken ct);

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

    /// <summary>Gets or sets the schema names the catalog records, in the order it records them.</summary>
    /// <remarks>
    /// A name the engine owns exists on every target that engine bootstrapped, so an adapter counts
    /// such a name as present whether or not it creates anything for it — a catalog captured before
    /// its adapter stopped recording those names still validates.
    /// </remarks>
    public IReadOnlyList<string> SchemaNames { get; init; } = [];

    /// <summary>Gets or sets the expected table row counts.</summary>
    public IReadOnlyDictionary<TableRef, long> TableRowCounts { get; init; } =
        new Dictionary<TableRef, long>();

    /// <summary>Gets or sets the expected sequence states.</summary>
    public IReadOnlyDictionary<ObjectRef, SequenceState> SequenceStates { get; init; } =
        new Dictionary<ObjectRef, SequenceState>();
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
