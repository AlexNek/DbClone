using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.Definitions;

/// <summary>
/// The comparison role: whether two tables agree. Both endpoints arrive on each call,
/// so one comparer serves any pair.
/// </summary>
public interface ITableComparer
{
    Task<TableCompareResult> CompareTableAsync(
        DatabaseTarget source,
        DatabaseTarget target,
        string schema,
        string table,
        EVerifyMode mode,
        CancellationToken ct);

    Task<long> CountRowsAsync(
        DatabaseTarget target,
        string schema,
        string table,
        CancellationToken ct);

    Task<List<string>> GetPrimaryKeyColumnsAsync(
        DatabaseTarget target,
        string schema,
        string table,
        CancellationToken ct);
}
