using System.Runtime.CompilerServices;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;

namespace DatabaseInterchange.Tests.Script;

/// <summary>
/// A renderer that answers one obvious statement per object and records the order it was asked in.
/// </summary>
/// <remarks>
/// The writer under test owns the layout, the ordering and the files; the SQL is the adapter's business and
/// is asserted in the adapter's own suite. What this double makes visible is only what the product decides:
/// which stages ran, in what order, and which table's rows were asked for after them.
/// </remarks>
internal sealed class RecordingScriptRenderer : ISqlScriptRenderer
{
    private readonly List<string> _calls = [];

    /// <summary>Initializes a new instance of the <see cref="RecordingScriptRenderer"/> class.</summary>
    /// <param name="dialect">The dialect the double claims to write.</param>
    internal RecordingScriptRenderer(string dialect = "postgresql")
    {
        Dialect = dialect;
    }

    /// <inheritdoc />
    public string Dialect { get; }

    /// <summary>
    /// Gets the name of the one object this double refuses to script, so a test can make the writer
    /// isolate the culprit out of a stage.
    /// </summary>
    internal string? Unscriptable { get; init; }

    /// <summary>
    /// Gets the name of the one table whose rows this double refuses, which is a refusal the writer only
    /// reaches after both scratch files are open.
    /// </summary>
    internal string? UnscriptableRows { get; init; }

    /// <summary>Gets every call the writer made, in order, as <c>schema &lt;stage&gt;:&lt;name&gt;</c>.</summary>
    internal IReadOnlyList<string> Calls => _calls;

    /// <summary>
    /// Gets the row count the writer stated for each table it opened, which is the catalog's own count
    /// rather than the number of rows handed over.
    /// </summary>
    internal Dictionary<string, long> DeclaredRowCounts { get; } = [];

    /// <summary>Gets how many rows one yielded statement carries; the whole table when not set.</summary>
    internal int BatchRows { get; init; } = int.MaxValue;

    /// <summary>Gets how many filler characters every insert statement carries, so a test can make its bytes tell.</summary>
    internal int StatementPadding { get; init; }

    /// <summary>Gets how many filler characters the tail statement carries, so a test can make its bytes tell.</summary>
    internal int TailPadding { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> HeaderStatements() => ["SET TIME ZONE 'UTC';"];

    /// <inheritdoc />
    public IReadOnlyList<string> SchemaStatements(SchemaStage stage, IReadOnlyList<ISchemaObject> objects)
    {
        var statements = new List<string>();

        foreach (var target in objects)
        {
            if (string.Equals(target.Name, Unscriptable, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"The {target.Kind} \"{target.Name}\" has no captured definition to write.");
            }

            _calls.Add($"schema {stage}:{target.Name}");
            statements.Add($"CREATE {stage} {target.Name};");
        }

        return statements;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> BeginTableBlock(TableDefinition table, long rowCount, ScriptBlockPart part)
    {
        var name = Name(table);

        DeclaredRowCounts[name] = rowCount;
        _calls.Add(part.IsPart ? $"begin {name}:part{part.Number}" : $"begin {name}");

        return [$"BEGIN {name};"];
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Statement, int Rows)> InsertStatements(
        TableDefinition table,
        IAsyncEnumerable<DataValue[]> rows,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var name = Name(table);

        if (string.Equals(table.Name, UnscriptableRows, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"The rows of the table \"{table.Name}\" have no scriptable shape.");
        }

        // The rows are counted as they pass, which is what proves the writer streams a table rather than
        // holding it: the double sees the stream, not a list.
        var batch = 0;

        await foreach (var _ in rows.WithCancellation(ct))
        {
            batch++;

            if (batch >= BatchRows)
            {
                _calls.Add($"data {name}:{batch}");
                yield return (Insert(name, batch), batch);
                batch = 0;
            }
        }

        if (batch > 0)
        {
            _calls.Add($"data {name}:{batch}");
            yield return (Insert(name, batch), batch);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EndTableBlock() => ["COMMIT BLOCK;"];

    /// <inheritdoc />
    public IReadOnlyList<string> TailStatements(
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<ViewDefinition> views)
    {
        _calls.Add("tail");

        return ["VALIDATE ALL KEYS;" + (TailPadding > 0 ? new string('x', TailPadding) : string.Empty)];
    }

    private static string Name(TableDefinition table) => $"{table.SchemaRef.ObjectName()}.{table.Name}";

    private string Insert(string name, int rows) =>
        $"INSERT INTO {name} ({rows} row(s));" + (StatementPadding > 0 ? new string('x', StatementPadding) : string.Empty);
}
