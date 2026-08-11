using DbClone.Application.Enums;
using DbClone.Application.TableFilter;

namespace DbClone.Application.DTOs;

/// <summary>
/// Options controlling the copy behavior.
/// </summary>
/// <param name="CopyData">Whether to copy table data rows.</param>
/// <param name="CopyIndexes">Creates secondary indexes. Primary key indexes are always created inline with CREATE TABLE regardless of this flag.</param>
/// <param name="CopyConstraints">Whether to copy CHECK, UNIQUE, and FOREIGN KEY constraints.</param>
/// <param name="CopyFunctions">Whether to copy user-defined functions.</param>
/// <param name="CopyTriggers">Whether to copy trigger definitions.</param>
/// <param name="CopyViews">Whether to copy view definitions.</param>
/// <param name="CopyMaterializedViews">Whether to copy materialized view definitions.</param>
/// <param name="CopySequences">Whether to copy SEQUENCE objects and their current values.</param>
/// <param name="CopyPolicies">Whether to copy row-level security policies.</param>
/// <param name="CopyComments">Whether to copy object COMMENT annotations.</param>
/// <param name="DropExisting">Whether to drop existing objects in the target before creating them.</param>
/// <param name="CopyMode">The copy mode that determines which objects are processed.</param>
/// <param name="VerifyMode">The verification mode used after copying table data.</param>
/// <param name="BatchSize">Number of rows per COPY batch.</param>
/// <param name="MaxDegreeOfParallelism">Maximum number of tables copied in parallel.</param>
/// <param name="ConstraintStrategy">Strategy for handling constraints during the copy.</param>
/// <param name="ExcludePlatformSchemas">When true, schemas owned by platform service roles are excluded from the copy.</param>
/// <param name="TableSelection">Optional table selection spec. Null means no filtering (copy all tables).</param>
public sealed record CopyOptions(
    bool CopyData = true,
    bool CopyIndexes = true,
    bool CopyConstraints = true,
    bool CopyFunctions = true,
    bool CopyTriggers = true,
    bool CopyViews = true,
    bool CopyMaterializedViews = true,
    bool CopySequences = true,
    bool CopyPolicies = true,
    bool CopyComments = true,
    bool DropExisting = false,
    ECopyMode CopyMode = ECopyMode.Full,
    EVerifyMode VerifyMode = EVerifyMode.RowCount,
    int BatchSize = 5000,
    int MaxDegreeOfParallelism = 4,
    EConstraintStrategy ConstraintStrategy = EConstraintStrategy.Automatic,
    bool ExcludePlatformSchemas = false,
    TableSelectionSpec? TableSelection = null);
