using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.Platforms;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.Metadata;

/// <summary>
/// PostgreSQL implementation of <see cref="IMetadataReader"/>.
/// Reads the complete database model from pg_catalog only — no dependency on
/// information_schema, which may be missing on databases created from template0
/// or where it was dropped manually.
/// Schemas are excluded dynamically when:
///   - they are PostgreSQL system internals (pg_catalog, information_schema, pg_toast), OR
///   - the current user lacks USAGE privilege, OR
///   - the schema owner is a role the current user is not a member of (platform-managed).
/// Exception: system schemas themselves ARE listed in <see cref="DatabaseModel.Schemas"/>
/// (flagged <see cref="SchemaDefinition.IsSystem"/>) so that consumers can verify their
/// presence — their contents are never read.
/// No platform-specific names are hardcoded (Open/Closed Principle).
/// </summary>
public sealed class PgMetadataReader : IMetadataReader
{
    private readonly PgSqlExecutor _executor;

    private readonly ILogger<PgMetadataReader> _logger;

    /// <summary>
    /// Dynamic SQL NOT IN list computed once per read. Includes universal system
    /// schemas plus any schema the current user lacks USAGE privilege on.
    /// </summary>
    private string _schemaFilter = PgSystemSchemas.SqlList;

    /// <summary>
    /// System schemas for the current read — used by ReadSchemasAsync to flag IsSystem.
    /// Falls back to <see cref="PgSystemSchemas.All"/> when no platform resolution is provided.
    /// </summary>
    private IReadOnlySet<string> _systemSchemas = PgSystemSchemas.AllSet;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgMetadataReader"/> class.
    /// </summary>
    public PgMetadataReader(PgSqlExecutor executor, ILogger<PgMetadataReader> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DatabaseModel> ReadDatabaseModelAsync(
        bool excludePlatformSchemas = false,
        PlatformResolution? platformResolution = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Reading database model (excludePlatformSchemas={Filter}, platform={Platform})",
            excludePlatformSchemas,
            platformResolution?.DetectedPlatform ?? "base");

        // Determine system schemas from resolution or fall back to hardcoded
        _systemSchemas = platformResolution?.SystemSchemas is { Count: > 0 } sys
            ? sys
            : PgSystemSchemas.AllSet;

        // Always build the schema filter — at minimum excludes system schemas.
        // Platform schemas and ownership heuristic are conditional.
        await BuildSchemaFilterAsync(platformResolution, excludePlatformSchemas, cancellationToken);

        var serverVersion = await _executor.ExecuteScalarAsync<string>(
                                "SHOW server_version",
                                cancellationToken);

        var dbName = await _executor.ExecuteScalarAsync<string>(
                         "SELECT current_database()",
                         cancellationToken);

        // Read extensions FIRST so we can widen search_path before reading columns.
        // pg_get_expr() uses the session search_path to decide whether to schema-qualify
        // function references in column defaults (e.g. uuid_generate_v4() vs
        // extensions.uuid_generate_v4()). By including extension schemas in search_path
        // we ensure defaults are stored unqualified — matching what the destination will
        // produce after CreateExtensionsStage sets the same search_path.
        var extensions = await ReadExtensionsAsync(cancellationToken);
        await WidenSearchPathForExtensionsAsync(extensions, cancellationToken);

        var schemas = await ReadSchemasAsync(cancellationToken);
        var enums = await ReadEnumsAsync(cancellationToken);
        var domains = await ReadDomainsAsync(cancellationToken);
        var compositeTypes = await ReadCompositeTypesAsync(cancellationToken);
        var sequences = await ReadSequencesAsync(cancellationToken);
        var tables = await ReadTablesAsync(cancellationToken);
        var views = await ReadViewsAsync(cancellationToken);
        var materializedViews = await ReadMaterializedViewsAsync(cancellationToken);
        var functions = await ReadFunctionsAsync(cancellationToken);
        var triggers = await ReadTriggersAsync(cancellationToken);
        var policies = await ReadPoliciesAsync(cancellationToken);
        var publications = await ReadPublicationsAsync(cancellationToken);
        var subscriptions = await ReadSubscriptionsAsync(cancellationToken);

        var model = new DatabaseModel(
            DatabaseName: dbName,
            ServerVersion: serverVersion,
            Schemas: schemas,
            Tables: tables,
            Views: views,
            MaterializedViews: materializedViews,
            Sequences: sequences,
            Enums: enums,
            Domains: domains,
            CompositeTypes: compositeTypes,
            Functions: functions,
            Triggers: triggers,
            Policies: policies,
            Publications: publications,
            Subscriptions: subscriptions,
            Extensions: extensions);

        _logger.LogInformation(
            "Read {TableCount} tables, {ViewCount} views, {FunctionCount} functions, {SchemaCount} schemas",
            tables.Count,
            views.Count,
            functions.Count,
            schemas.Count);

        return model;
    }

    /// <summary>
    /// Builds the dynamic SQL NOT IN filter. System schemas are always excluded.
    /// Platform schemas and the ownership heuristic are applied only when
    /// <paramref name="excludePlatformSchemas"/> is true.
    /// </summary>
    private async Task BuildSchemaFilterAsync(
        PlatformResolution? resolution,
        bool excludePlatformSchemas,
        CancellationToken ct)
    {
        // System schemas — ALWAYS excluded
        var excluded = new HashSet<string>(_systemSchemas, StringComparer.OrdinalIgnoreCase);

        if (excludePlatformSchemas)
        {
            // Platform schemas from .platform definition file
            if (resolution?.PlatformSchemas is { Count: > 0 } platformSchemas)
            {
                foreach (var s in platformSchemas)
                    excluded.Add(s);
            }

            // Ownership heuristic — supplement for unknown platforms
            await ProbeOwnershipHeuristicAsync(excluded, ct);
        }

        _schemaFilter = excluded.Count > 0
            ? string.Join(", ", excluded.Select(s => $"'{s.Replace("'", "''")}'"))
            : "''";

        if (excluded.Count > _systemSchemas.Count)
        {
            var platformExcluded = excluded.Except(
                _systemSchemas,
                StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation(
                "Excluding platform/system schemas: {Schemas}",
                string.Join(", ", platformExcluded));
        }
    }

    /// <summary>
    /// Probes schema accessibility and ownership, adding platform-managed schemas
    /// to the exclusion set. A schema is excluded if:
    ///   1. The current user lacks USAGE privilege, OR
    ///   2. The schema is platform-managed (ownership check):
    ///      - For non-superusers: owner is a role the user is NOT a member of.
    ///      - For superusers: owner is a different role that cannot login.
    /// </summary>
    private async Task ProbeOwnershipHeuristicAsync(HashSet<string> excluded, CancellationToken ct)
    {
        // Single query: get all schemas with owner info and USAGE privilege flag.
        var schemas = await _executor.QueryAsync(
                          @"SELECT n.nspname,
                     r.rolname      AS owner,
                     r.rolcanlogin  AS owner_can_login,
                     has_schema_privilege(current_user, n.oid, 'USAGE') AS has_usage
              FROM pg_namespace n
              JOIN pg_roles r ON r.oid = n.nspowner
              WHERE n.nspname NOT LIKE 'pg_temp_%'
                AND n.nspname NOT LIKE 'pg_toast_temp_%'",
                          r => (Name: r.GetString(0), Owner: r.GetString(1),
                                   OwnerCanLogin: r.GetBoolean(2), HasUsage: r.GetBoolean(3)),
                          ct);

        var currentUser = await _executor.ExecuteScalarAsync<string>("SELECT current_user", ct);
        var isSuperuser = await _executor.ExecuteScalarAsync<bool>(
                              "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
                              ct);

        HashSet<string> userOwnedOwners;
        if (isSuperuser)
        {
            userOwnedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentUser };
            foreach (var s in schemas)
            {
                if (s.OwnerCanLogin)
                    userOwnedOwners.Add(s.Owner);
            }
        }
        else
        {
            var distinctOwners = schemas
                .Select(s => s.Owner)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            userOwnedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in distinctOwners)
            {
                var isMember = await _executor.ExecuteScalarAsync<long>(
                                   $"SELECT CASE WHEN pg_has_role(current_user, '{owner.Replace("'", "''")}', 'MEMBER') THEN 1 ELSE 0 END",
                                   ct);
                if (isMember == 1)
                    userOwnedOwners.Add(owner);
            }
        }

        foreach (var s in schemas)
        {
            if (!s.HasUsage || !userOwnedOwners.Contains(s.Owner))
                excluded.Add(s.Name);
        }
    }

    private static IReadOnlyList<FunctionParameter> ParseFunctionParameters(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var parameters = new List<FunctionParameter>();
        var parts = SplitFunctionArguments(arguments);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var mode = EParameterMode.In;
            var upper = trimmed.ToUpperInvariant();

            if (upper.StartsWith("IN OUT ") || upper.StartsWith("INOUT "))
            {
                mode = EParameterMode.InOut;
                trimmed = trimmed[(upper.StartsWith("IN OUT ") ? 7 : 6)..];
            }
            else if (upper.StartsWith("OUT "))
            {
                mode = EParameterMode.Out;
                trimmed = trimmed[4..];
            }
            else if (upper.StartsWith("IN "))
            {
                mode = EParameterMode.In;
                trimmed = trimmed[3..];
            }
            else if (upper.StartsWith("VARIADIC "))
            {
                mode = EParameterMode.Variadic;
                trimmed = trimmed[9..];
            }

            var nameTypeParts = trimmed.Split(' ', 2);
            var name = nameTypeParts.Length > 1 ? nameTypeParts[0] : "";
            var dataType = nameTypeParts.Length > 1 ? nameTypeParts[1] : nameTypeParts[0];

            // Handle DEFAULT value
            string? defaultValue = null;
            var defaultIdx = dataType.IndexOf(" DEFAULT ", StringComparison.OrdinalIgnoreCase);
            if (defaultIdx > 0)
            {
                defaultValue = dataType[(defaultIdx + 9)..].Trim();
                dataType = dataType[..defaultIdx].Trim();
            }

            parameters.Add(
                new FunctionParameter(
                    Name: name,
                    DataType: dataType,
                    Mode: mode,
                    DefaultValue: defaultValue));
        }

        return parameters;
    }

    private static IReadOnlyList<string> ParseIndexColumns(string indexDef)
    {
        // Parse "CREATE [UNIQUE] INDEX ... ON ... USING btree (col1, col2)" or similar
        var openParen = indexDef.LastIndexOf('(');
        var closeParen = indexDef.LastIndexOf(')');
        if (openParen < 0 || closeParen < 0 || closeParen <= openParen)
            return [];

        var columnsPart = indexDef[(openParen + 1)..closeParen];

        // Check for WHERE clause after the closing paren
        var whereIdx = indexDef.IndexOf(" WHERE ", closeParen, StringComparison.OrdinalIgnoreCase);
        // We don't need WHERE here, we parse it separately

        return columnsPart.Split(',')
            .Select(c => c.Trim().Split(' ')[0]) // Remove ASC/DESC/NULLS options
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();
    }

    private static string? ParseIndexFilter(string indexDef)
    {
        var whereIdx = indexDef.IndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);
        if (whereIdx < 0)
            return null;

        return indexDef[(whereIdx + 7)..].Trim();
    }

    private async Task<Dictionary<string, IReadOnlyList<CheckConstraintDefinition>>>
        ReadCheckConstraintsAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                c.relname AS table_name,
                con.conname AS constraint_name,
                pg_get_constraintdef(con.oid) AS expression,
                con.condeferrable,
                con.condeferred
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'c'
              AND n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname, con.conname";

        var rows = await _executor.QueryAsync(
                       sql,
                       r =>
                           {
                               var key = $"{r.GetString(0)}.{r.GetString(1)}";
                               var check = new CheckConstraintDefinition(
                                   Name: r.GetString(2),
                                   Expression: r.GetString(3),
                                   IsDeferrable: r.GetBoolean(4),
                                   IsInitiallyDeferred: r.GetBoolean(5));
                               return (key, check);
                           },
                       ct);

        return rows
            .GroupBy(r => r.key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CheckConstraintDefinition>)g.Select(x => x.check).ToList());
    }

    private async Task<Dictionary<string, IReadOnlyList<ColumnDefinition>>> ReadColumnsAsync(
        CancellationToken ct)
    {
        // Uses pg_catalog exclusively — no dependency on information_schema which may
        // be missing on databases created from template0 or where it was dropped manually.
        // format_type(atttypid, atttypmod) gives the real PostgreSQL type notation:
        // text[], character varying(255), numeric(10,2), my_enum, etc.
        var sql = $@"
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                a.attname AS column_name,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                a.attnum AS ordinal_position,
                NOT a.attnotnull AS is_nullable,
                CASE WHEN a.attidentity = '' AND a.attgenerated = '' THEN pg_get_expr(d.adbin, d.adrelid) ELSE NULL END AS column_default,
                CASE WHEN a.attidentity IN ('a', 'd') THEN true ELSE false END AS is_identity,
                CASE WHEN a.attgenerated = 's' THEN true ELSE false END AS is_generated,
                CASE WHEN a.attgenerated = 's' THEN pg_get_expr(d.adbin, d.adrelid) ELSE NULL END AS generation_expression,
                col_description(c.oid, a.attnum) AS comment,
                a.attislocal AS is_local
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE c.relkind IN ({PgRelKind.TableOrPartition})
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname, a.attnum";

        var rows = await _executor.QueryAsync(
                       sql,
                       r =>
                           {
                               var key = $"{r.GetString(0)}.{r.GetString(1)}";
                               var col = new ColumnDefinition(
                                   Name: r.GetString(2),
                                   DataType: r.GetString(3),
                                   OrdinalPosition: r.GetInt16(4),
                                   IsNullable: r.GetBoolean(5),
                                   DefaultValue: r.IsDBNull(6) ? null : r.GetString(6),
                                   IsIdentity: r.GetBoolean(7),
                                   IsGenerated: r.GetBoolean(8),
                                   GenerationExpression: r.IsDBNull(9) ? null : r.GetString(9),
                                   Comment: r.IsDBNull(10) ? null : r.GetString(10),
                                   IsLocal: r.GetBoolean(11));
                               return (key, col);
                           },
                       ct);

        return rows
            .GroupBy(r => r.key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ColumnDefinition>)g.Select(x => x.col).ToList());
    }

    private async Task<IReadOnlyList<CompositeTypeDefinition>> ReadCompositeTypesAsync(
        CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                t.typname AS name,
                ARRAY(SELECT row(a.attname, format_type(a.atttypid, a.atttypmod), a.attnum, a.attnotnull)
                       FROM pg_attribute a
                       WHERE a.attrelid = t.typrelid AND a.attnum > 0 AND NOT a.attisdropped
                       ORDER BY a.attnum) AS attributes,
                obj_description(t.oid) AS comment
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_class c ON c.oid = t.typrelid
            WHERE t.typtype = 'c'
              AND c.relkind = '{PgRelKind.CompositeType}' -- only true composite types; excludes implicit row types of tables/views/matviews
              AND n.nspname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname, t.typname";

        // Read composite type attributes separately for clean parsing
        var attrSql = $@"
            SELECT
                n.nspname AS schema,
                t.typname AS type_name,
                a.attname AS attr_name,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                a.attnum AS ordinal,
                a.attnotnull
            FROM pg_attribute a
            JOIN pg_type t ON t.typrelid = a.attrelid
            JOIN pg_class c ON c.oid = t.typrelid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype = 'c'
              AND c.relkind = '{PgRelKind.CompositeType}' -- only true composite types
              AND a.attnum > 0 AND NOT a.attisdropped
              AND n.nspname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname, t.typname, a.attnum";

        var typeRows = await _executor.QueryAsync(
                           sql,
                           r =>
                               (Schema: r.GetString(0), Name: r.GetString(1),
                                   Comment: r.IsDBNull(3) ? null : r.GetString(3)),
                           ct);

        var attrRows = await _executor.QueryAsync(
                           attrSql,
                           r =>
                               (Key: $"{r.GetString(0)}.{r.GetString(1)}",
                                   Attr: new ColumnDefinition(
                                       Name: r.GetString(2),
                                       DataType: r.GetString(3),
                                       OrdinalPosition: r.GetInt32(4),
                                       IsNullable: !r.GetBoolean(5),
                                       DefaultValue: null,
                                       IsIdentity: false,
                                       IsGenerated: false,
                                       GenerationExpression: null,
                                       Comment: null)),
                           ct);

        var attrsByType = attrRows
            .GroupBy(r => r.Key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ColumnDefinition>)g.Select(x => x.Attr).ToList());

        return typeRows.Select(t =>
            {
                var key = $"{t.Schema}.{t.Name}";
                return new CompositeTypeDefinition(
                    SchemaName: t.Schema,
                    Name: t.Name,
                    Attributes: attrsByType.GetValueOrDefault(key, []),
                    Comment: t.Comment);
            }).ToList();
    }

    private async Task<IReadOnlyList<DomainDefinition>> ReadDomainsAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                t.typname AS name,
                format_type(t.typbasetype, t.typtypmod) AS data_type,
                t.typdefault,
                pg_get_constraintdef(con.oid) AS check_expression,
                NOT t.typnotnull AS is_nullable,
                obj_description(t.oid) AS comment
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_constraint con ON con.contypid = t.oid AND con.contype = 'c'
            WHERE t.typtype = 'd'
              AND n.nspname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname, t.typname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new DomainDefinition(
                           SchemaName: r.GetString(0),
                           Name: r.GetString(1),
                           DataType: r.GetString(2),
                           DefaultValue: r.IsDBNull(3) ? null : r.GetString(3),
                           CheckExpression: r.IsDBNull(4) ? null : r.GetString(4),
                           IsNullable: r.GetBoolean(5),
                           Comment: r.IsDBNull(6) ? null : r.GetString(6)),
                   ct);
    }

    private async Task<IReadOnlyList<EnumDefinition>> ReadEnumsAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                t.typname AS name,
                ARRAY(SELECT e.enumlabel FROM pg_enum e
                      WHERE e.enumtypid = t.oid ORDER BY e.enumsortorder) AS labels,
                obj_description(t.oid) AS comment
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype = 'e'
              AND n.nspname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname, t.typname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new EnumDefinition(
                           SchemaName: r.GetString(0),
                           Name: r.GetString(1),
                           Labels: r.GetFieldValue<string[]>(2).ToList(),
                           Comment: r.IsDBNull(3) ? null : r.GetString(3)),
                   ct);
    }

    private async Task<IReadOnlyList<ExtensionDefinition>> ReadExtensionsAsync(CancellationToken ct)
    {
        var sql = @"
            SELECT
                e.extname AS name,
                n.nspname AS schema,
                e.extversion AS version,
                obj_description(e.oid) AS comment
            FROM pg_extension e
            JOIN pg_namespace n ON n.oid = e.extnamespace
            ORDER BY e.extname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new ExtensionDefinition(
                           Name: r.GetString(0),
                           SchemaName: r.GetString(1),
                           Version: r.GetString(2),
                           Comment: r.IsDBNull(3) ? null : r.GetString(3)),
                   ct);
    }

    /// <summary>
    /// Widens the session search_path to include schemas that host extensions.
    /// This ensures pg_get_expr() returns unqualified function names for extension-
    /// provided functions (e.g. uuid_generate_v4() instead of extensions.uuid_generate_v4()).
    /// Must be called BEFORE reading columns/defaults so source DDL matches what the
    /// destination will produce after CreateExtensionsStage sets the same search_path.
    /// </summary>
    private async Task WidenSearchPathForExtensionsAsync(
        IReadOnlyList<ExtensionDefinition> extensions,
        CancellationToken ct)
    {
        var extensionSchemas = extensions
            .Select(e => e.SchemaName)
            .Where(s => !string.IsNullOrEmpty(s)
                        && !string.Equals(s, "public", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(s, "pg_catalog", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extensionSchemas.Count == 0)
            return;

        // Build: SET search_path TO "extensions", ..., public
        var searchPath = string.Join(
            ", ",
            extensionSchemas.Select(PgIdentifierQuoter.QuoteIdentifier).Append("public"));

        await _executor.ExecuteNonQueryAsync(
            $"SET search_path TO {searchPath}",
            ct);

        _logger.LogInformation(
            "Source search_path widened to {SearchPath} for consistent DDL output",
            searchPath);
    }


    private async Task<Dictionary<string, IReadOnlyList<ForeignKeyDefinition>>>
        ReadForeignKeysAsync(CancellationToken ct)
    {
        // Uses pg_catalog exclusively — no dependency on information_schema.
        // pg_constraint contains everything: FK columns, referenced table, update/delete rules.
        var sql = $@"
            SELECT
                n.nspname AS schema,
                c.relname AS table_name,
                con.conname AS constraint_name,
                ARRAY(SELECT a.attname FROM pg_attribute a
                      WHERE a.attrelid = con.conrelid AND a.attnum = ANY(con.conkey)
                      ORDER BY array_position(con.conkey, a.attnum)) AS columns,
                rn.nspname AS ref_schema,
                rc.relname AS ref_table,
                ARRAY(SELECT a.attname FROM pg_attribute a
                      WHERE a.attrelid = con.confrelid AND a.attnum = ANY(con.confkey)
                      ORDER BY array_position(con.confkey, a.attnum)) AS ref_columns,
                CASE con.confupdtype
                    WHEN 'a' THEN 'NO ACTION' WHEN 'r' THEN 'RESTRICT'
                    WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL'
                    WHEN 'd' THEN 'SET DEFAULT' ELSE 'NO ACTION' END AS update_rule,
                CASE con.confdeltype
                    WHEN 'a' THEN 'NO ACTION' WHEN 'r' THEN 'RESTRICT'
                    WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL'
                    WHEN 'd' THEN 'SET DEFAULT' ELSE 'NO ACTION' END AS delete_rule,
                con.condeferrable AS is_deferrable,
                con.condeferred AS is_initially_deferred
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_class rc ON rc.oid = con.confrelid
            JOIN pg_namespace rn ON rn.oid = rc.relnamespace
            WHERE con.contype = 'f'
              AND n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname, con.conname";

        var rows = await _executor.QueryAsync(
                       sql,
                       r =>
                           {
                               var key = $"{r.GetString(0)}.{r.GetString(1)}";
                               var columns = r.GetFieldValue<string[]>(3);
                               var refColumns = r.GetFieldValue<string[]>(6);

                               var fk = new ForeignKeyDefinition(
                                   Name: r.GetString(2),
                                   Columns: columns.ToList(),
                                   ReferencedSchema: r.GetString(4),
                                   ReferencedTable: r.GetString(5),
                                   ReferencedColumns: refColumns.ToList(),
                                   UpdateRule: r.GetString(7),
                                   DeleteRule: r.GetString(8),
                                   IsDeferrable: r.GetBoolean(9),
                                   IsInitiallyDeferred: r.GetBoolean(10));

                               return (key, fk);
                           },
                       ct);

        return rows
            .GroupBy(r => r.key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ForeignKeyDefinition>)g.Select(x => x.fk).ToList());
    }

    private async Task<IReadOnlyList<FunctionDefinition>> ReadFunctionsAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                p.proname AS name,
                l.lanname AS language,
                CASE p.prokind WHEN 'p' THEN 'void' ELSE format_type(p.prorettype, NULL) END AS return_type,
                pg_get_functiondef(p.oid) AS definition,
                p.provolatile::text,
                p.proisstrict,
                p.prosecdef,
                obj_description(p.oid) AS comment,
                CASE p.prokind WHEN 'p' THEN true ELSE false END AS is_procedure,
                pg_get_function_arguments(p.oid) AS arguments
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_language l ON l.oid = p.prolang
            WHERE n.nspname NOT IN ({_schemaFilter})
              AND l.lanname <> 'internal'
              AND l.lanname <> 'c'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')
            ORDER BY n.nspname, p.proname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       {
                           var volatility = r.GetString(5) switch
                               {
                                   "i" => EFunctionVolatility.Immutable,
                                   "s" => EFunctionVolatility.Stable,
                                   _ => EFunctionVolatility.Volatile
                               };

                           var parameters = ParseFunctionParameters(r.GetString(10));

                           return new FunctionDefinition(
                               SchemaName: r.GetString(0),
                               Name: r.GetString(1),
                               Language: r.GetString(2),
                               ReturnType: r.GetString(3),
                               Definition: r.GetString(4),
                               Parameters: parameters,
                               Volatility: volatility,
                               IsStrict: r.GetBoolean(6),
                               SecurityDefiner: r.GetBoolean(7),
                               Comment: r.IsDBNull(8) ? null : r.GetString(8),
                               IsProcedure: r.GetBoolean(9));
                       },
                   ct);
    }

    private async Task<Dictionary<string, IReadOnlyList<IndexDefinition>>> ReadIndexesAsync(
        CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema, t.relname AS table_name,
                i.relname AS index_name,
                ix.indisunique, ix.indisprimary,
                EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = ix.indexrelid) AS is_constraint,
                pg_get_indexdef(ix.indexrelid) AS index_def,
                CASE WHEN i.reltablespace = 0 THEN '' ELSE i.reltablespace::regclass::text END AS tablespace
            FROM pg_index ix
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname NOT IN ({_schemaFilter})
              AND t.relkind IN ({PgRelKind.TableOrPartition})
            ORDER BY n.nspname, t.relname, i.relname";

        var rows = await _executor.QueryAsync(
                       sql,
                       r =>
                           {
                               var key = $"{r.GetString(0)}.{r.GetString(1)}";
                               var indexName = r.GetString(2);
                               var isUnique = r.GetBoolean(3);
                               var isPrimary = r.GetBoolean(4);
                               var isConstraint = r.GetBoolean(5);
                               var indexDef = r.GetString(6);
                               var tablespace = r.GetString(7);

                               // Parse column names from pg_get_indexdef (used for comparison signatures)
                               var columns = ParseIndexColumns(indexDef);

                               // Parse filter (WHERE clause) from pg_get_indexdef
                               var filter = ParseIndexFilter(indexDef);

                               var idx = new IndexDefinition(
                                   Name: indexName,
                                   Columns: columns,
                                   IsUnique: isUnique,
                                   IsPrimary: isPrimary,
                                   FilterExpression: filter,
                                   Tablespace: string.IsNullOrEmpty(tablespace) ? null : tablespace,
                                   Definition: indexDef,
                                   IsConstraint: isConstraint);

                               return (key, idx);
                           },
                       ct);

        return rows
            .GroupBy(r => r.key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IndexDefinition>)g.Select(x => x.idx).ToList());
    }

    private async Task<IReadOnlyList<MaterializedViewDefinition>> ReadMaterializedViewsAsync(
        CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                c.relname AS name,
                pg_get_viewdef(c.oid) AS definition,
                CASE WHEN c.reltablespace = 0 THEN '' ELSE c.reltablespace::regclass::text END AS tablespace,
                ARRAY(SELECT a.attname FROM pg_attribute a
                      WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                      ORDER BY a.attnum) AS columns,
                obj_description(c.oid) AS comment,
                ARRAY(
                    SELECT DISTINCT nr.nspname || '.' || cr.relname
                    FROM pg_depend d
                    JOIN pg_rewrite r ON r.oid = d.objid
                    JOIN pg_class cr ON cr.oid = d.refobjid
                    JOIN pg_namespace nr ON nr.oid = cr.relnamespace
                    WHERE r.ev_class = c.oid
                      AND d.refclassid = 'pg_class'::regclass
                      AND cr.oid <> c.oid
                      AND cr.relkind IN ({PgRelKind.AllUserRelations})
                      AND nr.nspname NOT IN ({_schemaFilter})
                ) AS referenced_relations
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = '{PgRelKind.MaterializedView}'
              AND n.nspname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')
            ORDER BY n.nspname, c.relname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new MaterializedViewDefinition(
                           SchemaName: r.GetString(0),
                           Name: r.GetString(1),
                           Definition: r.GetString(2),
                           Tablespace: string.IsNullOrEmpty(r.GetString(3)) ? null : r.GetString(3),
                           Columns: r.GetFieldValue<string[]>(4).ToList(),
                           Comment: r.IsDBNull(5) ? null : r.GetString(5),
                           ReferencedRelations: r.GetFieldValue<string[]>(6).ToList()),
                   ct);
    }

    private async Task<IReadOnlyList<PolicyDefinition>> ReadPoliciesAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                p.polname AS name,
                c.relname AS table_name,
                CASE p.polcmd
                    WHEN 'r' THEN 'SELECT'
                    WHEN 'a' THEN 'INSERT'
                    WHEN 'w' THEN 'UPDATE'
                    WHEN 'd' THEN 'DELETE'
                    ELSE 'ALL'
                END AS command,
                p.polpermissive AS is_permissive,
                ARRAY(SELECT r.rolname FROM pg_roles r WHERE r.oid = ANY(p.polroles)) AS roles,
                pg_get_expr(p.polqual, p.polrelid) AS qual,
                pg_get_expr(p.polwithcheck, p.polrelid) AS with_check
            FROM pg_policy p
            JOIN pg_class c ON c.oid = p.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname, p.polname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new PolicyDefinition(
                           SchemaName: r.GetString(0),
                           Name: r.GetString(1),
                           TableName: r.GetString(2),
                           Command: r.GetString(3),
                           IsPermissive: r.GetBoolean(4),
                           Roles: r.GetFieldValue<string[]>(5).ToList(),
                           QualExpression: r.IsDBNull(6) ? null : r.GetString(6),
                           WithCheckExpression: r.IsDBNull(7) ? null : r.GetString(7)),
                   ct);
    }

    private async Task<IReadOnlyList<PublicationDefinition>> ReadPublicationsAsync(
        CancellationToken ct)
    {
        var sql = @"
            SELECT
                p.pubname AS name,
                p.puballtables AS is_for_all_tables,
                ARRAY(SELECT pn.nspname || '.' || pc.relname
                      FROM pg_publication_rel pr
                      JOIN pg_class pc ON pc.oid = pr.prrelid
                      JOIN pg_namespace pn ON pn.oid = pc.relnamespace
                      WHERE pr.prpubid = p.oid) AS tables,
                obj_description(p.oid) AS comment
            FROM pg_publication p
            ORDER BY p.pubname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new PublicationDefinition(
                           Name: r.GetString(0),
                           IsForAllTables: r.GetBoolean(1),
                           Tables: r.GetFieldValue<string[]>(2).ToList(),
                           Comment: r.IsDBNull(3) ? null : r.GetString(3)),
                   ct);
    }

    private async Task<IReadOnlyList<SchemaDefinition>> ReadSchemasAsync(CancellationToken ct)
    {
        // System schemas are included here with IsSystem = true so their PRESENCE
        // can be verified (e.g. a dropped information_schema). Their contents are
        // never read — every other metadata query keeps excluding them via _schemaFilter.
        var systemSqlList = string.Join(", ", _systemSchemas.Select(s => $"'{s}'"));
        var sql = $@"
            SELECT n.nspname, r.rolname AS owner,
                   obj_description(n.oid) AS comment,
                   n.nspname IN ({systemSqlList}) AS is_system
            FROM pg_namespace n
            JOIN pg_roles r ON r.oid = n.nspowner
            WHERE (n.nspname NOT IN ({_schemaFilter})
                   OR n.nspname IN ({systemSqlList}))
              AND n.nspname NOT LIKE 'pg_temp_%'
              AND n.nspname NOT LIKE 'pg_toast_temp_%'
            ORDER BY n.nspname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new SchemaDefinition(
                           Name: r.GetString(0),
                           Owner: r.GetString(1),
                           Comment: r.IsDBNull(2) ? null : r.GetString(2),
                           IsSystem: r.GetBoolean(3)),
                   ct);
    }

    private async Task<IReadOnlyList<SequenceDefinition>> ReadSequencesAsync(CancellationToken ct)
    {
        // deptype 'i' = internal (identity column backing sequence — non-deterministic name)
        // deptype 'a' = auto (serial / OWNED BY sequence — deterministic name, explicit object)
        var sql = $@"
            SELECT
                n.nspname AS schema,
                c.relname AS name,
                s.seqstart, s.seqincrement,
                CASE WHEN s.seqmin <> 1 OR s.seqmax <> 9223372036854775807
                     THEN s.seqmin ELSE NULL END AS min_value,
                CASE WHEN s.seqmin <> 1 OR s.seqmax <> 9223372036854775807
                     THEN s.seqmax ELSE NULL END AS max_value,
                s.seqcache, s.seqcycle,
                format_type(s.seqtypid, NULL) AS data_type,
                obj_description(c.oid) AS comment,
                tn.nspname AS owner_schema,
                tc.relname AS owner_table,
                a.attname AS owner_column,
                d.deptype AS dep_type
            FROM pg_sequence s
            JOIN pg_class c ON c.oid = s.seqrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_depend d ON d.objid = s.seqrelid
                AND d.classid = 'pg_class'::regclass
                AND d.refclassid = 'pg_class'::regclass
                AND d.refobjsubid > 0
                AND d.deptype IN ('i', 'a')
            LEFT JOIN pg_class tc ON tc.oid = d.refobjid
            LEFT JOIN pg_namespace tn ON tn.oid = tc.relnamespace
            LEFT JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
            WHERE n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                   {
                       var ownerSchema = r.IsDBNull(10) ? null : r.GetString(10);
                       var ownerTable = r.IsDBNull(11) ? null : r.GetString(11);
                       var ownerColumn = r.IsDBNull(12) ? null : r.GetString(12);
                       var depType = r.IsDBNull(13) ? (char?)null : r.GetChar(13);
                       var qualifiedOwner = ownerSchema is not null && ownerTable is not null
                           ? $"{ownerSchema}.{ownerTable}"
                           : null;

                       return new SequenceDefinition(
                           SchemaName: r.GetString(0),
                           Name: r.GetString(1),
                           StartValue: r.GetInt64(2),
                           IncrementBy: r.GetInt64(3),
                           MinValue: r.IsDBNull(4) ? null : r.GetInt64(4),
                           MaxValue: r.IsDBNull(5) ? null : r.GetInt64(5),
                           CacheSize: r.GetInt64(6),
                           IsCycled: r.GetBoolean(7),
                           DataType: r.IsDBNull(8) ? null : r.GetString(8),
                           Comment: r.IsDBNull(9) ? null : r.GetString(9),
                           OwnerTable: qualifiedOwner,
                           OwnerColumn: ownerColumn,
                           IsIdentity: depType == 'i');
                   },
                   ct);
    }

    private async Task<IReadOnlyList<SubscriptionDefinition>> ReadSubscriptionsAsync(
        CancellationToken ct)
    {
        try
        {
            var sql = @"
                SELECT
                    s.subname AS name,
                    s.subconninfo AS connection_info,
                    s.subpublications[1] AS publication,
                    s.subenabled AS is_enabled,
                    obj_description(s.oid) AS comment
                FROM pg_subscription s
                ORDER BY s.subname";

            return await _executor.QueryAsync(
                       sql,
                       r =>
                           new SubscriptionDefinition(
                               Name: r.GetString(0),
                               ConnectionString: r.GetString(1),
                               PublicationName: r.IsDBNull(2) ? "" : r.GetString(2),
                               IsEnabled: r.GetBoolean(3),
                               Comment: r.IsDBNull(4) ? null : r.GetString(4)),
                       ct);
        }
        catch
        {
            _logger.LogWarning("Could not read subscriptions (requires superuser)");
            return [];
        }
    }

    private async Task<IReadOnlyList<TableDefinition>> ReadTablesAsync(CancellationToken ct)
    {
        // Read basic table info, including partition hierarchy:
        // - parent schema/name via pg_inherits (for child partitions)
        // - partition bound via pg_get_expr(relpartbound) e.g. "FOR VALUES FROM (...) TO (...)" or "DEFAULT"
        var sql = $@"
            SELECT n.nspname AS schema, c.relname AS name,
                   CASE c.relkind WHEN '{PgRelKind.PartitionedTable}' THEN true ELSE false END AS is_partitioned,
                   CASE c.relkind WHEN '{PgRelKind.PartitionedTable}' THEN pg_get_partkeydef(c.oid) ELSE NULL END AS partition_strategy,
                   obj_description(c.oid) AS comment,
                   pn.nspname AS parent_schema,
                   pc.relname AS parent_name,
                   pg_get_expr(c.relpartbound, c.oid) AS partition_bound
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_inherits inh ON inh.inhrelid = c.oid
            LEFT JOIN pg_class pc ON pc.oid = inh.inhparent
            LEFT JOIN pg_namespace pn ON pn.oid = pc.relnamespace
            WHERE c.relkind IN ({PgRelKind.TableOrPartition})
              AND n.nspname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')
            ORDER BY n.nspname, c.relname";

        var tables = await _executor.QueryAsync(
                         sql,
                         r =>
                             {
                                 var schema = r.GetString(0);
                                 var name = r.GetString(1);
                                 var parentSchema = r.IsDBNull(5) ? null : r.GetString(5);
                                 var parentName = r.IsDBNull(6) ? null : r.GetString(6);
                                 return new TableDefinition(
                                     SchemaName: schema,
                                     Name: name,
                                     Columns: [],
                                     Indexes: [],
                                     ForeignKeys: [],
                                     CheckConstraints: [],
                                     UniqueConstraints: [],
                                     Comment: r.IsDBNull(4) ? null : r.GetString(4),
                                     IsPartitioned: r.GetBoolean(2),
                                     PartitionStrategy: r.IsDBNull(3) ? null : r.GetString(3),
                                     ParentTable: parentSchema is null || parentName is null
                                                      ? null
                                                      : $"{parentSchema}.{parentName}",
                                     PartitionBound: r.IsDBNull(7) ? null : r.GetString(7));
                             },
                         ct);

        // Read columns for all tables
        var columnsByTable = await ReadColumnsAsync(ct);

        // Read indexes for all tables
        var indexesByTable = await ReadIndexesAsync(ct);

        // Read foreign keys
        var fksByTable = await ReadForeignKeysAsync(ct);

        // Read check constraints
        var checksByTable = await ReadCheckConstraintsAsync(ct);

        // Read unique constraints
        var uniquesByTable = await ReadUniqueConstraintsAsync(ct);

        // Assemble tables with their children
        return tables.Select(t =>
            {
                var key = $"{t.SchemaName}.{t.Name}";
                return t with
                           {
                               Columns = columnsByTable.GetValueOrDefault(key, []),
                               Indexes = indexesByTable.GetValueOrDefault(key, []),
                               ForeignKeys = fksByTable.GetValueOrDefault(key, []),
                               CheckConstraints = checksByTable.GetValueOrDefault(key, []),
                               UniqueConstraints = uniquesByTable.GetValueOrDefault(key, [])
                           };
            }).ToList();
    }

    private async Task<IReadOnlyList<TriggerDefinition>> ReadTriggersAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                t.tgname AS name,
                c.relname AS table_name,
                CASE
                    WHEN t.tgtype & 2 = 2 THEN 'BEFORE'
                    WHEN t.tgtype & 2 = 0 THEN 'AFTER'
                    ELSE 'INSTEAD OF'
                END AS timing,
                CASE WHEN t.tgtype & 4 = 4 THEN true ELSE false END AS is_insert,
                CASE WHEN t.tgtype & 8 = 8 THEN true ELSE false END AS is_delete,
                CASE WHEN t.tgtype & 16 = 16 THEN true ELSE false END AS is_update,
                CASE WHEN t.tgtype & 1 = 1 THEN true ELSE false END AS is_row,
                t.tgenabled <> 'D' AS is_enabled,
                pg_get_triggerdef(t.oid) AS trigger_def,
                pn.nspname AS func_schema,
                pf.proname AS func_name,
                obj_description(t.oid) AS comment
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_proc pf ON pf.oid = t.tgfoid
            JOIN pg_namespace pn ON pn.oid = pf.pronamespace
            WHERE NOT t.tgisinternal
              AND n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname, t.tgname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       {
                           var events = new List<string>();
                           if (r.GetBoolean(4)) events.Add("INSERT");
                           if (r.GetBoolean(5)) events.Add("DELETE");
                           if (r.GetBoolean(6)) events.Add("UPDATE");

                           return new TriggerDefinition(
                               SchemaName: r.GetString(0),
                               Name: r.GetString(1),
                               TableName: r.GetString(2),
                               Timing: r.GetString(3),
                               Events: events,
                               FunctionSchema: r.GetString(10),
                               FunctionName: r.GetString(11),
                               IsRowLevel: r.GetBoolean(7),
                               IsEnabled: r.GetBoolean(8),
                               Condition: null,
                               Comment: r.IsDBNull(12) ? null : r.GetString(12));
                       },
                   ct);
    }

    private async Task<Dictionary<string, IReadOnlyList<UniqueConstraintDefinition>>>
        ReadUniqueConstraintsAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                n.nspname AS schema,
                c.relname AS table_name,
                con.conname AS constraint_name,
                ARRAY(SELECT a.attname
                      FROM unnest(con.conkey) WITH ORDINALITY AS u(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = u.attnum
                      ORDER BY u.ord) AS columns,
                con.condeferrable,
                con.condeferred
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'u'
              AND n.nspname NOT IN ({_schemaFilter})
            ORDER BY n.nspname, c.relname, con.conname";

        var rows = await _executor.QueryAsync(
                       sql,
                       r =>
                           {
                               var key = $"{r.GetString(0)}.{r.GetString(1)}";
                               var columns = r.GetFieldValue<string[]>(3);
                               var unique = new UniqueConstraintDefinition(
                                   Name: r.GetString(2),
                                   Columns: columns.ToList(),
                                   IsDeferrable: r.GetBoolean(4),
                                   IsInitiallyDeferred: r.GetBoolean(5));
                               return (key, unique);
                           },
                       ct);

        return rows
            .GroupBy(r => r.key)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<UniqueConstraintDefinition>)g.Select(x => x.unique).ToList());
    }

    private async Task<IReadOnlyList<ViewDefinition>> ReadViewsAsync(CancellationToken ct)
    {
        var sql = $@"
            SELECT
                schemaname AS schema,
                viewname AS name,
                definition,
                obj_description(c.oid) AS comment,
                ARRAY(
                    SELECT DISTINCT nr.nspname || '.' || cr.relname
                    FROM pg_depend d
                    JOIN pg_rewrite r ON r.oid = d.objid
                    JOIN pg_class cr ON cr.oid = d.refobjid
                    JOIN pg_namespace nr ON nr.oid = cr.relnamespace
                    WHERE r.ev_class = c.oid
                      AND d.refclassid = 'pg_class'::regclass
                      AND cr.oid <> c.oid
                      AND cr.relkind IN ({PgRelKind.AllUserRelations})
                      AND nr.nspname NOT IN ({_schemaFilter})
                ) AS referenced_relations
            FROM pg_views v
            JOIN pg_class c ON c.relname = v.viewname
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = v.schemaname
            WHERE v.schemaname NOT IN ({_schemaFilter})
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')
            ORDER BY v.schemaname, v.viewname";

        return await _executor.QueryAsync(
                   sql,
                   r =>
                       new ViewDefinition(
                           SchemaName: r.GetString(0),
                           Name: r.GetString(1),
                           Definition: r.GetString(2),
                           Comment: r.IsDBNull(3) ? null : r.GetString(3),
                           ReferencedRelations: r.GetFieldValue<string[]>(4).ToList()),
                   ct);
    }

    private static List<string> SplitFunctionArguments(string arguments)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(arguments[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < arguments.Length)
            result.Add(arguments[start..]);

        return result;
    }
}
