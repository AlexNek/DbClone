using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Exceptions;
using DbClone.Application.Interfaces;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace DbClone.PostgreSql.Providers;

public sealed class PgTableComparerProvider : ITableComparerProvider
{
    private readonly ILogger<PgTableComparerProvider> _logger;

    public PgTableComparerProvider(ILogger<PgTableComparerProvider> logger)
    {
        _logger = logger;
    }

    public async Task<TableCompareResult> CompareTableAsync(
        ConnectionInfo source,
        ConnectionInfo dest,
        string schema,
        string table,
        EVerifyMode mode,
        CancellationToken ct)
    {
        var srcBuilder = PgConnectionStringBuilder.BuildConnectionString(source);
        var dstBuilder = PgConnectionStringBuilder.BuildConnectionString(dest);

        await using var sourceConn = new NpgsqlConnection(srcBuilder.ConnectionString);
        await using var destConn = new NpgsqlConnection(dstBuilder.ConnectionString);
        await sourceConn.OpenAsync(ct);
        await destConn.OpenAsync(ct);

        var qualifiedName = $"\"{schema}\".\"{table}\"";

        long sourceCount;
        try
        {
            await using var sourceCountCmd = new NpgsqlCommand(
                BuildCountSql(qualifiedName),
                sourceConn);
            sourceCount = (long)(await sourceCountCmd.ExecuteScalarAsync(ct))!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToCompareException(ECompareSide.Source, ex);
        }

        long destCount;
        try
        {
            await using var destCountCmd = new NpgsqlCommand(
                BuildCountSql(qualifiedName),
                destConn);
            destCount = (long)(await destCountCmd.ExecuteScalarAsync(ct))!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToCompareException(ECompareSide.Destination, ex);
        }

        if (sourceCount == 0 && destCount == 0)
            return new TableCompareResult(true, 0, 0, 0, 0, 0);

        if (mode == EVerifyMode.RowCount)
            return new TableCompareResult(
                sourceCount == destCount,
                sourceCount,
                destCount,
                0,
                0,
                0);

        if (mode == EVerifyMode.Checksum)
        {
            if (sourceCount != destCount)
                return new TableCompareResult(false, sourceCount, destCount, 0, 0, 0);

            var checksumSql =
                $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t";
            await using var srcChecksumCmd = new NpgsqlCommand(checksumSql, sourceConn);
            var sourceChecksum = await srcChecksumCmd.ExecuteScalarAsync(ct) as string;

            await using var dstChecksumCmd = new NpgsqlCommand(checksumSql, destConn);
            var destChecksum = await dstChecksumCmd.ExecuteScalarAsync(ct) as string;

            return new TableCompareResult(
                sourceChecksum == destChecksum,
                sourceCount,
                destCount,
                0,
                0,
                0);
        }

        var pkColumns = await GetPrimaryKeyColumnsAsync(source, schema, table, ct);
        if (pkColumns.Count == 0)
        {
            var checksumSql =
                $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t";
            await using var srcChecksumCmd = new NpgsqlCommand(checksumSql, sourceConn);
            var sourceChecksum = await srcChecksumCmd.ExecuteScalarAsync(ct) as string;

            await using var dstChecksumCmd = new NpgsqlCommand(checksumSql, destConn);
            var destChecksum = await dstChecksumCmd.ExecuteScalarAsync(ct) as string;

            return new TableCompareResult(
                sourceChecksum == destChecksum,
                sourceCount,
                destCount,
                0,
                0,
                0);
        }

        var pkList = string.Join(", ", pkColumns.Select(c => $"\"{c}\""));
        var pkSelectSql = $"SELECT {pkList} FROM {qualifiedName}";

        var sourcePks = new HashSet<string>();
        var destPks = new HashSet<string>();

        await using (var srcPkCmd = new NpgsqlCommand(pkSelectSql, sourceConn))
        await using (var reader = await srcPkCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var pkValues = new string[pkColumns.Count];
                for (int i = 0; i < pkColumns.Count; i++)
                    pkValues[i] = reader.GetValue(i)?.ToString() ?? "NULL";
                sourcePks.Add(string.Join("|", pkValues));
            }
        }

        await using (var dstPkCmd = new NpgsqlCommand(pkSelectSql, destConn))
        await using (var reader = await dstPkCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var pkValues = new string[pkColumns.Count];
                for (int i = 0; i < pkColumns.Count; i++)
                    pkValues[i] = reader.GetValue(i)?.ToString() ?? "NULL";
                destPks.Add(string.Join("|", pkValues));
            }
        }

        var onlyInSource = sourcePks.Except(destPks).Count();
        var onlyInDest = destPks.Except(sourcePks).Count();
        var commonPks = sourcePks.Intersect(destPks).Count();

        long rowsAdded = onlyInDest;
        long rowsRemoved = onlyInSource;
        long rowsModified = 0;

        if (commonPks > 0)
        {
            var checksumSql =
                $"SELECT md5(string_agg(t::text, '' ORDER BY t::text)) FROM {qualifiedName} t";
            await using var srcChecksumCmd = new NpgsqlCommand(checksumSql, sourceConn);
            var sourceChecksum = await srcChecksumCmd.ExecuteScalarAsync(ct) as string;

            await using var dstChecksumCmd = new NpgsqlCommand(checksumSql, destConn);
            var destChecksum = await dstChecksumCmd.ExecuteScalarAsync(ct) as string;

            if (sourceChecksum != destChecksum)
            {
                rowsModified = Math.Abs(sourceCount - destCount - rowsAdded + rowsRemoved);
                if (rowsModified == 0)
                    rowsModified = 1;
            }
        }

        bool isMatch = rowsAdded == 0 && rowsRemoved == 0 && rowsModified == 0;
        return new TableCompareResult(
            isMatch,
            sourceCount,
            destCount,
            rowsAdded,
            rowsRemoved,
            rowsModified);
    }

    public async Task<long> CountRowsAsync(
        ConnectionInfo connection,
        string schema,
        string table,
        CancellationToken ct)
    {
        var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        var qualifiedName = $"\"{schema}\".\"{table}\"";
        await using var cmd = new NpgsqlCommand(BuildCountSql(qualifiedName), conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<List<string>> GetPrimaryKeyColumnsAsync(
        ConnectionInfo connection,
        string schema,
        string table,
        CancellationToken ct)
    {
        var builder = PgConnectionStringBuilder.BuildConnectionString(connection);
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        var columns = new List<string>();
        try
        {
            await using var cmd = new NpgsqlCommand(
                @"SELECT a.attname
                  FROM pg_index i
                  JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                  WHERE i.indrelid = (quote_ident($1) || '.' || quote_ident($2))::regclass
                    AND i.indisprimary",
                conn);
            cmd.Parameters.AddWithValue(schema);
            cmd.Parameters.AddWithValue(table);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                columns.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not retrieve primary key columns for {Schema}.{Table}",
                schema,
                table);
        }

        return columns;
    }

    /// <summary>
    /// Builds the row-count query used for source/destination comparison.
    /// Deliberately WITHOUT ONLY: for a legacy-inheritance parent the count must include
    /// child rows visible through inheritance. The source parent sees its children's rows,
    /// so the destination parent must too — a dropped INHERITS relationship therefore
    /// surfaces as a count mismatch instead of passing silently.
    /// </summary>
    internal static string BuildCountSql(string qualifiedName) =>
        $"SELECT count(*) FROM {qualifiedName}";

    /// <summary>
    /// Wraps a provider exception in a <see cref="TableCompareException"/> carrying the
    /// failing side and SQLSTATE as structured fields. For <see cref="PostgresException"/>
    /// the clean server message (<see cref="PostgresException.MessageText"/>) is used so the
    /// SQLSTATE code never leaks into the message string.
    /// </summary>
    private static TableCompareException ToCompareException(ECompareSide side, Exception ex)
    {
        if (ex is PostgresException pg)
            return new TableCompareException(side, pg.MessageText, pg.SqlState, ex);
        return new TableCompareException(side, ex.Message, null, ex);
    }
}
