using System.Text.Json;

namespace CanonicalModel.Interchange;

/// <summary>
/// Database information in the interchange model.
/// </summary>
public sealed record DatabaseInfo
{
    /// <summary>Gets or sets the database name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the database collation.</summary>
    public string? Collation { get; init; }

    /// <summary>Gets or sets the database encoding.</summary>
    public string? Encoding { get; init; }

    /// <summary>Gets or sets the database time zone.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Gets or sets the server version string.</summary>
    public string? ServerVersion { get; init; }
}

/// <summary>
/// Environment information recorded at export time.
/// </summary>
public sealed record EnvironmentInfo
{
    /// <summary>Gets or sets the export timestamp.</summary>
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>Gets or sets the producer identifier.</summary>
    public string Producer { get; init; } = string.Empty;

    /// <summary>Gets or sets the hostname.</summary>
    public string? Hostname { get; init; }

    /// <summary>Gets or sets additional properties.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// The root aggregate of the interchange model — a complete database snapshot.
/// </summary>
public sealed record InterchangeSnapshot
{
    /// <summary>Gets or sets the format version.</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>Gets or sets the minimum reader version.</summary>
    public int MinimumReader { get; init; } = 1;

    /// <summary>Gets or sets the producer identifier.</summary>
    public string ProducedBy { get; init; } = string.Empty;

    /// <summary>Gets or sets the database information.</summary>
    public DatabaseInfo Database { get; init; } = new();

    /// <summary>Gets or sets the schemas.</summary>
    public IReadOnlyList<SchemaDefinition> Schemas { get; init; } = [];

    /// <summary>Gets or sets the tables.</summary>
    public IReadOnlyList<TableDefinition> Tables { get; init; } = [];

    /// <summary>Gets or sets the views.</summary>
    public IReadOnlyList<ViewDefinition> Views { get; init; } = [];

    /// <summary>Gets or sets the routines.</summary>
    public IReadOnlyList<RoutineDefinition> Routines { get; init; } = [];

    /// <summary>Gets or sets the sequences.</summary>
    public IReadOnlyList<SequenceDefinition> Sequences { get; init; } = [];

    /// <summary>Gets or sets the extensions.</summary>
    public IReadOnlyList<ExtensionDefinition> Extensions { get; init; } = [];

    /// <summary>Gets or sets the relationships (dependency edges).</summary>
    public IReadOnlyList<Relationship> Relationships { get; init; } = [];

    /// <summary>Gets or sets the environment information.</summary>
    public EnvironmentInfo? Environment { get; init; }

    /// <summary>Gets or sets the security section.</summary>
    public SecuritySection? Security { get; init; }

    /// <summary>Gets or sets the platform section.</summary>
    public PlatformSection? Platform { get; init; }

    /// <summary>Gets or sets issues recorded during export.</summary>
    public IReadOnlyList<InterchangeIssue> Issues { get; init; } = [];
}

/// <summary>
/// The schema half of a snapshot, returned by an adapter's ReadSchemaAsync.
/// The exporter assembles this into a full InterchangeSnapshot.
/// </summary>
public sealed record SnapshotSchema
{
    /// <summary>Gets or sets the database information.</summary>
    public DatabaseInfo Database { get; init; } = new();

    /// <summary>Gets or sets the schemas.</summary>
    public IReadOnlyList<SchemaDefinition> Schemas { get; init; } = [];

    /// <summary>Gets or sets the tables.</summary>
    public IReadOnlyList<TableDefinition> Tables { get; init; } = [];

    /// <summary>Gets or sets the views.</summary>
    public IReadOnlyList<ViewDefinition> Views { get; init; } = [];

    /// <summary>Gets or sets the routines.</summary>
    public IReadOnlyList<RoutineDefinition> Routines { get; init; } = [];

    /// <summary>Gets or sets the sequences.</summary>
    public IReadOnlyList<SequenceDefinition> Sequences { get; init; } = [];

    /// <summary>Gets or sets the extensions.</summary>
    public IReadOnlyList<ExtensionDefinition> Extensions { get; init; } = [];

    /// <summary>Gets or sets the relationships.</summary>
    public IReadOnlyList<Relationship> Relationships { get; init; } = [];

    /// <summary>Gets or sets the security section.</summary>
    public SecuritySection? Security { get; init; }
}

/// <summary>
/// An extension in the interchange model.
/// </summary>
public sealed record ExtensionDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Extension;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the extension version.</summary>
    public string? Version { get; init; }

    /// <summary>Gets or sets the extension schema.</summary>
    public string? Schema { get; init; }
}

/// <summary>
/// A dependency relationship between two objects.
/// </summary>
public sealed record Relationship
{
    /// <summary>Gets or sets the dependent object reference.</summary>
    public ObjectRef DependentRef { get; init; }

    /// <summary>Gets or sets the dependency object reference.</summary>
    public ObjectRef DependencyRef { get; init; }

    /// <summary>Gets or sets the relationship type.</summary>
    public string Type { get; init; } = string.Empty;
}

/// <summary>
/// Security section of a snapshot.
/// </summary>
public sealed record SecuritySection
{
    /// <summary>Gets or sets the roles/principals.</summary>
    public IReadOnlyList<RoleDefinition> Roles { get; init; } = [];

    /// <summary>Gets or sets the grants/permissions.</summary>
    public IReadOnlyList<GrantDefinition> Grants { get; init; } = [];

    /// <summary>Gets or sets the RLS policies.</summary>
    public IReadOnlyList<PolicyDefinition> Policies { get; init; } = [];
}

/// <summary>
/// A role/principal.
/// </summary>
public sealed record RoleDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Role;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets whether this is a superuser.</summary>
    public bool IsSuperuser { get; init; }

    /// <summary>Gets or sets whether this role can create databases.</summary>
    public bool CanCreateDb { get; init; }

    /// <summary>Gets or sets whether this role can create roles.</summary>
    public bool CanCreateRole { get; init; }

    /// <summary>Gets or sets whether this role can login.</summary>
    public bool CanLogin { get; init; }

    /// <summary>Gets or sets the member-of role names.</summary>
    public IReadOnlyList<string> MemberOf { get; init; } = [];
}

/// <summary>
/// A grant/permission.
/// </summary>
public sealed record GrantDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Grant;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the grantee role reference.</summary>
    public ObjectRef GranteeRef { get; init; }

    /// <summary>Gets or sets the object reference being granted access to.</summary>
    public ObjectRef TargetRef { get; init; }

    /// <summary>Gets or sets the privileges.</summary>
    public IReadOnlyList<string> Privileges { get; init; } = [];

    /// <summary>Gets or sets whether the grant is with grant option.</summary>
    public bool WithGrantOption { get; init; }
}

/// <summary>
/// An RLS policy.
/// </summary>
public sealed record PolicyDefinition : ISchemaObject
{
    /// <inheritdoc />
    public ObjectRef Ref { get; init; }

    /// <inheritdoc />
    public EObjectKind Kind => EObjectKind.Policy;

    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? SourceObjectId { get; init; }

    /// <inheritdoc />
    public NativeDefinition? NativeDefinition { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <inheritdoc />
    public IReadOnlyList<InterchangeIssue> Notes { get; init; } = [];

    /// <summary>Gets or sets the table reference this policy applies to.</summary>
    public ObjectRef TableRef { get; init; }

    /// <summary>Gets or sets the command this policy applies to (ALL, SELECT, INSERT, etc.).</summary>
    public string Command { get; init; } = "ALL";

    /// <summary>Gets or sets whether the policy is permissive.</summary>
    public bool IsPermissive { get; init; } = true;

    /// <summary>Gets or sets the applicable role names.</summary>
    public IReadOnlyList<string> ApplicableRoles { get; init; } = [];

    /// <summary>Gets or sets the USING expression.</summary>
    public DatabaseExpression? Using { get; init; }

    /// <summary>Gets or sets the WITH CHECK expression.</summary>
    public DatabaseExpression? WithCheck { get; init; }
}

/// <summary>
/// Platform section for platform-specific resources.
/// </summary>
public sealed record PlatformSection
{
    /// <summary>Gets or sets the platform identifier.</summary>
    public string? PlatformId { get; init; }

    /// <summary>Gets or sets platform-specific resources.</summary>
    public IReadOnlyDictionary<string, JsonElement> Resources { get; init; } =
        new Dictionary<string, JsonElement>();
}
