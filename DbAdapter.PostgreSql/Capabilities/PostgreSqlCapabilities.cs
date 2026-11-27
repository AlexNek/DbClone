using CanonicalModel.Interchange;

using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Capabilities;

/// <summary>
/// PostgreSQL adapter capabilities.
/// </summary>
public sealed class PostgreSqlCapabilities : IDatabaseCapabilities
{
    /// <inheritdoc />
    public bool CanRead(EObjectKind kind) => kind switch
    {
        EObjectKind.Schema or
        EObjectKind.Table or
        EObjectKind.Column or
        EObjectKind.View or
        EObjectKind.MaterializedView or
        EObjectKind.Function or
        EObjectKind.Procedure or
        EObjectKind.Sequence or
        EObjectKind.Trigger or
        EObjectKind.PrimaryKey or
        EObjectKind.ForeignKey or
        EObjectKind.UniqueConstraint or
        EObjectKind.CheckConstraint or
        EObjectKind.Index or
        EObjectKind.Extension or
        EObjectKind.Role or
        EObjectKind.Grant or
        EObjectKind.Policy or
        EObjectKind.Domain or
        EObjectKind.Enum or
        EObjectKind.CompositeType => true,
        _ => false,
    };

    /// <inheritdoc />
    public bool CanPreserve(EObjectKind kind) => CanRead(kind);

    /// <inheritdoc />
    public bool CanCreate(EObjectKind kind) => CanRead(kind);

    /// <inheritdoc />
    public bool CanRestoreData => true;

    /// <inheritdoc />
    public bool CanRestoreSecurity => true;

    /// <inheritdoc />
    public IReadOnlyList<ESnapshotConsistency> SupportedSnapshotModes =>
    [
        ESnapshotConsistency.ConsistentSnapshot,
        ESnapshotConsistency.BestEffort,
        ESnapshotConsistency.SchemaOnly,
    ];

    /// <inheritdoc />
    public bool SupportsTransactionalImport => true;

    /// <inheritdoc />
    public bool SupportsExistingTarget => false;

    /// <inheritdoc />
    public IReadOnlyList<ERowOrder> SupportedRowOrders =>
    [
        ERowOrder.PrimaryKey,
        ERowOrder.CanonicalValueOrder,
        ERowOrder.SourceOrder,
    ];

    /// <inheritdoc />
    public IReadOnlyList<SchemaStage> SupportedSchemaStages =>
    [
        SchemaStage.Schemas,
        SchemaStage.Extensions,
        SchemaStage.Types,
        SchemaStage.Sequences,
        SchemaStage.Tables,
        SchemaStage.Routines,
        SchemaStage.Views,
        SchemaStage.Constraints,
        SchemaStage.Indexes,
        SchemaStage.Triggers,
        SchemaStage.PostData,
    ];

    /// <inheritdoc />
    public IReadOnlyList<ESecurityStage> SupportedSecurityStages =>
    [
        ESecurityStage.Principals,
        ESecurityStage.Ownership,
        ESecurityStage.RowLevelSecurity,
        ESecurityStage.Grants,
    ];
}
