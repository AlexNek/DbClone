using DbClone.Application.DTOs;
using DbClone.Application.Enums;

namespace DbClone.Application.Interfaces;

public interface ITableComparerProvider
{
    Task<TableCompareResult> CompareTableAsync(
        ConnectionInfo source,
        ConnectionInfo dest,
        string schema,
        string table,
        EVerifyMode mode,
        CancellationToken ct);

    Task<long> CountRowsAsync(
        ConnectionInfo connection,
        string schema,
        string table,
        CancellationToken ct);

    Task<List<string>> GetPrimaryKeyColumnsAsync(
        ConnectionInfo connection,
        string schema,
        string table,
        CancellationToken ct);
}
