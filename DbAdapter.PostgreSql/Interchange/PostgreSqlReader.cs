using System.Runtime.CompilerServices;

using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;

using DbAdapter.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

using Npgsql;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseReader"/>.
/// <para>
/// The reader adds no metadata or data query of its own to the ones the plugin already has: the
/// object inventory comes from <see cref="IMetadataSource"/>, the same role the copy and compare
/// features use, and only the rows are read here, with one query per table. The conversion from
/// the engine's operational model to the catalog's preservation model lives in
/// <see cref="DatabaseModelMapper"/>; the reader's own work is the streaming and the encoding of
/// values into the text forms the format specifies.
/// </para>
/// </summary>
public sealed class PostgreSqlReader : IDatabaseReader
{
    /// <summary>
    /// Rows fetched per round trip when streaming a table. A server-side cursor keeps memory flat
    /// whatever the table size, which is what makes a large export possible at all.
    /// </summary>
    private const int CursorFetchRows = 1000;

    private readonly ILogger<PostgreSqlReader> _logger;
    private readonly DatabaseTarget _target;
    private readonly ExportOptions _options;
    private readonly IMetadataSource _metadataSource;
    private readonly DatabaseModelMapper _mapper;
    private Models.DatabaseModel? _model;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private int _cursorSequence;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlReader"/> class.
    /// </summary>
    public PostgreSqlReader(
        IDatabaseAdapter adapter,
        DatabaseTarget target,
        ExportOptions options,
        ILogger<PostgreSqlReader> logger,
        IMetadataSource metadataSource)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
        _mapper = new DatabaseModelMapper(adapter.Info.Dialect);
    }

    /// <inheritdoc />
    public IDatabaseAdapter Adapter { get; }

    /// <inheritdoc />
    public ICapabilityDetector CapabilityDetector => throw new NotSupportedException(
        "Capability detector requires an active connection. Use the copy pipeline for live operations.");

    /// <inheritdoc />
    public async Task<SnapshotSchema> ReadSchemaAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _logger.LogInformation("Reading schema from PostgreSQL database");

        var model = await EnsureModelAsync(ct);
        return _mapper.Map(model);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NativeObjectDefinition>> ReadNativeObjectsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var model = await EnsureModelAsync(ct);
        var natives = _mapper.MapNativeObjects(model);

        _logger.LogInformation(
            "Captured {NativeCount} native-only object definitions",
            natives.Count);

        return natives;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DataValue[]> ReadTableRowsAsync(
        TableRef table,
        ERowOrder order,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var model = await EnsureModelAsync(ct);
        var source = FindTable(model, table);

        await EnsureConnectionAsync(ct);
        _logger.LogInformation(
            "Reading rows from {Table} with order {Order}",
            $"{table.Schema}.{table.Name}",
            order);

        var columns = source.Columns.OrderBy(c => c.OrdinalPosition).ToList();
        var logicalTypes = columns
            .Select(c => PgLogicalTypeMap.Classify(c.DataType, BuildEnumTypeNames(model)))
            .ToArray();

        // A declared cursor, not a plain reader: PostgreSQL's result for a whole table would
        // otherwise be buffered, and the export is expected to hold flat memory whatever the table
        // size. FETCH pulls a bounded batch per round trip.
        var cursorName = $"interchange_cursor_{++_cursorSequence}";
        var selectSql = BuildSelectSql(source, columns, order);

        await using (
            var declare = new NpgsqlCommand(
                $"DECLARE {cursorName} CURSOR FOR {selectSql}",
                _connection,
                _transaction))
        {
            await declare.ExecuteNonQueryAsync(ct);
        }

        try
        {
            while (true)
            {
                var rowsInBatch = 0;

                await using (
                    var fetch = new NpgsqlCommand(
                        $"FETCH FORWARD {CursorFetchRows} FROM {cursorName}",
                        _connection,
                        _transaction)
                    {
                        CommandTimeout = 300,
                    })
                await using (var reader = await fetch.ExecuteReaderAsync(
                    System.Data.CommandBehavior.SequentialAccess, ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var row = new DataValue[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            if (await reader.IsDBNullAsync(i, ct))
                            {
                                row[i] = DataValue.Null(logicalTypes[i]);
                            }
                            else if (IsTextCarrier(logicalTypes[i]))
                            {
                                // R6: oversized text values stream through a TextReader in
                                // fixed-size chunks instead of becoming one large GetString.
                                // The token text stays byte-identical to col::text output.
                                using var textReader = reader.GetTextReader(i);
                                var sb = new System.Text.StringBuilder();
                                var buffer = new char[8192];
                                int read;
                                while ((read = await textReader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    sb.Append(buffer, 0, read);
                                }

                                row[i] = DataValue.FromText(logicalTypes[i], sb.ToString());
                            }
                            else
                            {
                                row[i] = DataValue.FromText(logicalTypes[i], reader.GetString(i));
                            }
                        }

                        rowsInBatch++;

                        // The catalog writer consumes one row per enumerated item; chunking is its
                        // job, not the reader's.
                        yield return row;
                    }
                }

                if (rowsInBatch == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            // Best-effort: an abandoned enumeration or a broken connection releases the cursor when
            // the transaction goes away, so a failure here must not mask the original error.
            try
            {
                await using var close = new NpgsqlCommand(
                    $"CLOSE {cursorName}",
                    _connection,
                    _transaction);
                await close.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not close cursor {Cursor}", cursorName);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    /// <summary>
    /// Reads the model once and keeps it: the schema pass, the native pass and every table's row
    /// pass describe the same database and must not disagree about it.
    /// </summary>
    private async Task<Models.DatabaseModel> EnsureModelAsync(CancellationToken ct) =>
        _model ??= await _metadataSource.ReadDatabaseModelAsync(excludePlatformSchemas: false, ct);

    /// <summary>
    /// Opens the read connection and the transaction that holds the export's cursors.
    /// </summary>
    /// <remarks>
    /// The schema is read through <see cref="IMetadataSource"/>, which owns its own short-lived
    /// connection, so DDL and rows are not guaranteed to come from the same instant. Closing that
    /// gap needs a connection-supplying overload on the metadata role, which is Phase 12 work; the
    /// transaction here at least makes the row counts across tables mutually consistent. A
    /// transaction is required either way, because a declared cursor lives inside one.
    /// </remarks>
    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            return;
        }

        var builder = PgConnectionStringBuilder.ParseConnectionString(_target);
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        _connection = connection;

        _transaction = await connection.BeginTransactionAsync(
            _options.RequestedConsistency == ESnapshotConsistency.ConsistentSnapshot
                ? System.Data.IsolationLevel.RepeatableRead
                : System.Data.IsolationLevel.ReadCommitted,
            ct);

        // Intervals are exported in the ISO-8601 form the format specifies, and PostgreSQL's own
        // session setting is the only faithful way to get it. LOCAL scopes the change to this
        // transaction on this connection, so the server's default is untouched.
        await using var style = new NpgsqlCommand(
            "SET LOCAL intervalstyle = 'iso_8601'",
            connection,
            _transaction);
        await style.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Builds the row query. Values are cast to text by the server, which is how the format's
    /// "typed text token, never a CLR object" rule is satisfied without re-implementing
    /// PostgreSQL's own output functions — and it is the same technique the copy pipeline uses.
    /// </summary>
    private static string BuildSelectSql(
        Models.TableDefinition source,
        IReadOnlyList<Models.ColumnDefinition> columns,
        ERowOrder order)
    {
        var projections = columns.Select(c => Project(c, Quote(c.Name)));
        var selectList = string.Join(", ", projections);
        var qualifiedName = PgIdentifierQuoter.QuoteSchemaQualified(source.SchemaName, source.Name);

        var orderBy = order switch
        {
            ERowOrder.SourceOrder => null,
            ERowOrder.CanonicalValueOrder => string.Join(", ", columns.Select(c => Quote(c.Name))),
            _ => PrimaryKeyOrder(source, columns),
        };

        return orderBy is null
                   ? $"SELECT {selectList} FROM {qualifiedName}"
                   : $"SELECT {selectList} FROM {qualifiedName} ORDER BY {orderBy}";
    }

    /// <summary>
    /// Renders one column as the text the catalog stores. A <c>timestamptz</c> is converted to UTC
    /// first, because the format records that type as a UTC instant rather than as the session's
    /// local rendering of it.
    /// </summary>
    private static string Project(Models.ColumnDefinition column, string quoted) =>
        PgLogicalTypeMap.IsUtcNormalized(column.DataType)
            ? $"({quoted} AT TIME ZONE 'UTC')::text"
            : $"{quoted}::text";

    /// <summary>
    /// Orders a keyless table by every column instead of promising an order it cannot repeat; the
    /// caller declares <see cref="ERowOrder.SourceOrder"/> when even that is impossible.
    /// </summary>
    private static string? PrimaryKeyOrder(
        Models.TableDefinition source,
        IReadOnlyList<Models.ColumnDefinition> columns)
    {
        var primaryKey = source.Indexes.FirstOrDefault(i => i.IsPrimary);
        var known = new HashSet<string>(columns.Select(c => c.Name), StringComparer.Ordinal);

        var usable = primaryKey?.Columns is { Count: > 0 }
                         ? primaryKey.Columns.Where(known.Contains).ToList()
                         : [];

        if (usable is { Count: > 0 } && usable.Count == primaryKey!.Columns.Count)
        {
            return string.Join(", ", usable.Select(Quote));
        }

        return string.Join(", ", columns.Select(c => Quote(c.Name)));
    }

    private static string Quote(string identifier) =>
        PgIdentifierQuoter.QuoteIdentifier(identifier);

    private static IReadOnlySet<string> BuildEnumTypeNames(Models.DatabaseModel model)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enumType in model.Enums)
        {
            names.Add(enumType.Name);
            names.Add($"{enumType.SchemaName}.{enumType.Name}");
        }

        return names;
    }

    private static Models.TableDefinition FindTable(Models.DatabaseModel model, TableRef table)
    {
        var found = model.Tables.FirstOrDefault(
            t => string.Equals(t.SchemaName, table.Schema, StringComparison.Ordinal)
                 && string.Equals(t.Name, table.Name, StringComparison.Ordinal));

        return found ?? throw new InvalidOperationException(
            $"Table {table} is not part of the captured schema.");
    }

    /// <summary>
    /// Says whether a logical type is a text carrier — a type whose stored token is the
    /// verbatim <c>col::text</c> output and which may therefore be very large. Text carriers
    /// are read through <c>GetTextReader</c> in fixed-size chunks (R6) instead of through
    /// <c>GetString</c>, so a 50 MB value does not appear in memory as one allocation.
    /// <c>Binary</c> is not a text carrier: part B stores it as bytes in a container instead
    /// of streaming a hex token.
    /// </summary>
    private static bool IsTextCarrier(ELogicalType type) =>
        type is ELogicalType.String
            or ELogicalType.Json
            or ELogicalType.Xml
            or ELogicalType.BitString
            or ELogicalType.Unknown;
}
