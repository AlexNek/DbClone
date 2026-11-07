using CanonicalModel.Interchange;

using DbAdapter.Definitions.Interchange;
using DbAdapter.PostgreSql.Execution;

using Models = CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// Extracts native (dialect-specific) objects from a <see cref="Models.DatabaseModel"/>.
/// These objects cannot be converted to neutral format and must be preserved as native SQL.
/// <para>
/// Most of this text is assembled from the catalog rows the metadata reader reported, because
/// PostgreSQL reports no <c>CREATE</c> statement for a type or a domain the way it does for a function,
/// an index or a trigger. Identifiers are therefore quoted through <see cref="PgIdentifierQuoter"/>, so
/// a name the source stored in mixed case or equal to a reserved word survives the round trip, and an
/// enum label is written as an escaped string literal. What an assembled statement cannot say — a
/// publication's publish settings, a subscription's connection string — is declared by
/// <see cref="NativeDefinition.Complete"/> and a note rather than left out.
/// </para>
/// <para>
/// Kinds the structured sections already carry are built here too and filtered out by
/// <see cref="DatabaseModelMapper.MapNativeObjects"/>, which keeps one source of truth per object for
/// the catalog.
/// </para>
/// </summary>
public sealed class NativeObjectExtractor
{
    private readonly string _dialect;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeObjectExtractor"/> class.
    /// </summary>
    /// <param name="dialect">The source dialect (e.g., "postgresql").</param>
    public NativeObjectExtractor(string dialect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialect);
        _dialect = dialect;
    }

    /// <summary>
    /// Extracts all non-portable objects from the database model.
    /// </summary>
    /// <param name="model">The database model.</param>
    /// <returns>A list of native object definitions.</returns>
    public IReadOnlyList<NativeObjectDefinition> Extract(Models.DatabaseModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var nativeObjects = new List<NativeObjectDefinition>();

        // Extract functions and procedures
        foreach (var func in model.Functions)
        {
            nativeObjects.Add(ExtractFunction(func));
        }

        // Extract triggers
        foreach (var trigger in model.Triggers)
        {
            nativeObjects.Add(ExtractTrigger(trigger));
        }

        // Extract views
        foreach (var view in model.Views)
        {
            nativeObjects.Add(ExtractView(view));
        }

        // Extract materialized views
        foreach (var matView in model.MaterializedViews)
        {
            nativeObjects.Add(ExtractMaterializedView(matView));
        }

        // Extract extensions
        foreach (var ext in model.Extensions)
        {
            nativeObjects.Add(ExtractExtension(ext));
        }

        // Extract policies
        foreach (var policy in model.Policies)
        {
            nativeObjects.Add(ExtractPolicy(policy));
        }

        // Extract publications
        foreach (var pub in model.Publications)
        {
            nativeObjects.Add(ExtractPublication(pub));
        }

        // Extract subscriptions
        foreach (var sub in model.Subscriptions)
        {
            nativeObjects.Add(ExtractSubscription(sub));
        }

        // Extract sequences
        foreach (var seq in model.Sequences)
        {
            nativeObjects.Add(ExtractSequence(seq));
        }

        // Extract enums
        foreach (var enumType in model.Enums)
        {
            nativeObjects.Add(ExtractEnum(enumType));
        }

        // Extract domains
        foreach (var domain in model.Domains)
        {
            nativeObjects.Add(ExtractDomain(domain));
        }

        // Extract composite types
        foreach (var compType in model.CompositeTypes)
        {
            nativeObjects.Add(ExtractCompositeType(compType));
        }

        return nativeObjects;
    }

    private NativeObjectDefinition ExtractFunction(Models.FunctionDefinition func) =>
        // This text came back from pg_get_functiondef as the engine's own complete statement, so it is
        // handed on instead of rebuilt.
        Record(
            func.IsProcedure ? EObjectKind.Procedure : EObjectKind.Function,
            func.SchemaName,
            func.Name,
            func.Definition,
            capturedBy: "pg_get_functiondef");

    private NativeObjectDefinition ExtractTrigger(Models.TriggerDefinition trigger)
    {
        // Reconstruct CREATE TRIGGER statement
        var sql = $@"CREATE TRIGGER {trigger.Name}
    {trigger.Timing} {string.Join(" OR ", trigger.Events)} ON {trigger.SchemaName}.{trigger.TableName}
    {(trigger.IsRowLevel ? "FOR EACH ROW" : "FOR EACH STATEMENT")}
    {(trigger.Condition is not null ? $"WHEN ({trigger.Condition})" : string.Empty)}
    EXECUTE FUNCTION {trigger.FunctionSchema}.{trigger.FunctionName}()";

        // The function this text calls is written with no arguments, and neither an UPDATE OF column
        // list nor a deferrability clause has a field to come from, so the rebuild is a fragment of what
        // the source declared.
        return Record(
            EObjectKind.Trigger,
            trigger.SchemaName,
            trigger.Name,
            sql,
            complete: false,
            dependencies: [$"{trigger.FunctionSchema}.{trigger.FunctionName}"]);
    }

    private NativeObjectDefinition ExtractView(Models.ViewDefinition view) =>
        // The Definition property is the query body pg_get_viewdef reported, not a CREATE statement.
        Record(
            EObjectKind.View,
            view.SchemaName,
            view.Name,
            view.Definition,
            complete: false,
            dependencies: view.ReferencedRelations,
            capturedBy: "pg_get_viewdef");

    private NativeObjectDefinition ExtractMaterializedView(Models.MaterializedViewDefinition matView) =>
        // The Definition property is the query body pg_get_viewdef reported, not a CREATE statement.
        Record(
            EObjectKind.MaterializedView,
            matView.SchemaName,
            matView.Name,
            matView.Definition,
            complete: false,
            capturedBy: "pg_get_viewdef");

    private NativeObjectDefinition ExtractExtension(Models.ExtensionDefinition ext)
    {
        var sql = $"CREATE EXTENSION IF NOT EXISTS {ext.Name}";
        if (!string.IsNullOrEmpty(ext.SchemaName))
        {
            sql += $" SCHEMA {ext.SchemaName}";
        }

        if (!string.IsNullOrEmpty(ext.Version))
        {
            sql += $" VERSION '{ext.Version}'";
        }

        // The catalog row says which extension is installed, in which schema and at which version; it
        // does not say what the statement that installed it declared, so this is one fragment of it.
        return Record(
            EObjectKind.Extension,
            ext.SchemaName ?? string.Empty,
            ext.Name,
            sql,
            complete: false);
    }

    private NativeObjectDefinition ExtractPolicy(Models.PolicyDefinition policy)
    {
        // Reconstruct CREATE POLICY statement
        var sql = $"CREATE POLICY {policy.Name} ON {policy.SchemaName}.{policy.TableName}";

        if (!string.IsNullOrEmpty(policy.Command))
        {
            sql += $" FOR {policy.Command}";
        }

        if (policy.Roles is { Count: > 0 })
        {
            sql += $" TO {string.Join(", ", policy.Roles)}";
        }

        if (!string.IsNullOrEmpty(policy.QualExpression))
        {
            sql += $" USING ({policy.QualExpression})";
        }

        if (!string.IsNullOrEmpty(policy.WithCheckExpression))
        {
            sql += $" WITH CHECK ({policy.WithCheckExpression})";
        }

        // Neither AS PERMISSIVE nor TO PUBLIC has a field of its own to come from, so a policy rebuilt
        // this way can end up looser or tighter than the source wrote it.
        return Record(
            EObjectKind.Policy,
            policy.SchemaName,
            policy.Name,
            sql,
            complete: false,
            dependencies: [$"{policy.SchemaName}.{policy.TableName}"]);
    }

    private NativeObjectDefinition ExtractPublication(Models.PublicationDefinition pub)
    {
        var sql = $"CREATE PUBLICATION {PgIdentifierQuoter.QuoteIdentifier(pub.Name)}";

        if (pub.IsForAllTables)
        {
            sql += " FOR ALL TABLES";
        }
        else if (pub.Tables is { Count: > 0 })
        {
            sql += $" FOR TABLE {string.Join(", ", pub.Tables)}";
        }
        else
        {
            // A CREATE PUBLICATION with no FOR clause publishes every table that is later created, so
            // leaving the clause off would turn a publication of nothing into one of everything.
            sql += " FOR NOTHING";
        }

        return Record(
            EObjectKind.Publication,
            string.Empty,
            pub.Name,
            sql,
            complete: false,
            dependencies: pub.Tables?.ToList(),
            notes: [
                new InterchangeIssue(
                    "PublicationSettingsNotCaptured",
                    EIssueSeverity.Warning,
                    $"Publication {pub.Name} was captured with the tables it publishes and nothing "
                    + "else: which operations it publishes, and whether it publishes through a "
                    + "partitioned table's root, are not part of the metadata model."),
            ]);
    }

    private NativeObjectDefinition ExtractSubscription(Models.SubscriptionDefinition sub) =>
        // The statement the engine would need carries the publisher connection string inside it, and
        // that string holds the credentials of another database. A catalog is a file that gets copied,
        // so the text is not recorded here: the subscription's name, the publication it reads and a
        // note saying what was left out are.
        Record(
            EObjectKind.Subscription,
            string.Empty,
            sub.Name,
            statement: null,
            dependencies: [sub.PublicationName],
            notes: [
                new InterchangeIssue(
                    "CredentialOmitted",
                    EIssueSeverity.Warning,
                    $"Subscription {sub.Name} was captured without its statement, because "
                    + "CREATE SUBSCRIPTION embeds the publisher connection string and the credentials "
                    + "inside it. Its name and the publication it reads are recorded."),
            ]);

    private NativeObjectDefinition ExtractSequence(Models.SequenceDefinition seq)
    {
        // Reconstruct CREATE SEQUENCE statement
        var sql = $"CREATE SEQUENCE {seq.SchemaName}.{seq.Name}";

        if (seq.DataType is not null)
        {
            sql += $" AS {seq.DataType}";
        }

        sql += $" START WITH {seq.StartValue}";
        sql += $" INCREMENT BY {seq.IncrementBy}";

        if (seq.MinValue.HasValue)
        {
            sql += $" MINVALUE {seq.MinValue}";
        }
        else
        {
            sql += " NO MINVALUE";
        }

        if (seq.MaxValue.HasValue)
        {
            sql += $" MAXVALUE {seq.MaxValue}";
        }
        else
        {
            sql += " NO MAXVALUE";
        }

        sql += $" CACHE {seq.CacheSize}";

        if (seq.IsCycled)
        {
            sql += " CYCLE";
        }

        // The declaration alone: which column owns the sequence, and what it has counted to, are in no
        // part of this text.
        return Record(
            EObjectKind.Sequence,
            seq.SchemaName,
            seq.Name,
            sql,
            complete: false);
    }

    private NativeObjectDefinition ExtractEnum(Models.EnumDefinition enumDef)
    {
        // The reader reports the labels in enumsortorder, which is the order the type was declared
        // with, so the statement below lists them the way the source listed them.
        var values = string.Join(", ", enumDef.Labels.Select(EnumLabel));
        var sql =
            $"CREATE TYPE {PgIdentifierQuoter.QuoteSchemaQualified(enumDef.SchemaName, enumDef.Name)} "
            + $"AS ENUM ({values})";

        return Record(EObjectKind.Enum, enumDef.SchemaName, enumDef.Name, sql);
    }

    /// <summary>
    /// Writes one enum label as the string literal the statement wants it to be: an apostrophe inside a
    /// label is doubled, which is escaping for a literal and not the quoting an identifier gets.
    /// </summary>
    private static string EnumLabel(string label) => $"'{label.Replace("'", "''")}'";

    private NativeObjectDefinition ExtractDomain(Models.DomainDefinition domain)
    {
        var sql =
            $"CREATE DOMAIN {PgIdentifierQuoter.QuoteSchemaQualified(domain.SchemaName, domain.Name)} "
            + $"AS {domain.DataType}";

        // PostgreSQL puts the default before the constraints of a domain clause, and the constraint text
        // the reader reported already begins with CHECK, so it is written whole instead of wrapped.
        if (domain.DefaultValue is { Length: > 0 })
        {
            sql += $" DEFAULT {domain.DefaultValue}";
        }

        if (!domain.IsNullable)
        {
            sql += " NOT NULL";
        }

        if (domain.CheckExpression is { Length: > 0 })
        {
            sql += $" {domain.CheckExpression}";
        }

        return Record(
            EObjectKind.Domain,
            domain.SchemaName,
            domain.Name,
            sql,
            notes: [
                // The statement is whole for what the metadata model reports about the domain; a
                // collation the base type was given with is not among what it reports.
                new InterchangeIssue(
                    "DomainCollationNotCaptured",
                    EIssueSeverity.Info,
                    $"Domain {domain.SchemaName}.{domain.Name} was captured from the base type, "
                    + "default, nullability and check the metadata model reports; a COLLATE clause "
                    + "the source declared it with is not part of that model."),
            ]);
    }

    private NativeObjectDefinition ExtractCompositeType(Models.CompositeTypeDefinition compType)
    {
        // The attributes come in attnum order, which is the order the type was declared with, and each
        // keeps the NOT NULL a composite attribute can carry.
        var columns = string.Join(
            ", ",
            compType.Attributes.Select(a =>
                $"{PgIdentifierQuoter.QuoteIdentifier(a.Name)} {a.DataType}"
                + (a.IsNullable ? string.Empty : " NOT NULL")));

        var sql =
            $"CREATE TYPE {PgIdentifierQuoter.QuoteSchemaQualified(compType.SchemaName, compType.Name)} "
            + $"AS ({columns})";

        return Record(EObjectKind.CompositeType, compType.SchemaName, compType.Name, sql);
    }

    /// <summary>
    /// Records one object together with the statement this pass assembled or handed on for it.
    /// </summary>
    /// <param name="kind">The neutral kind of the object.</param>
    /// <param name="schemaName">The schema holding it, empty for one the database keeps globally.</param>
    /// <param name="name">The object's name.</param>
    /// <param name="statement">The statement text, or <c>null</c> when writing it down would write down
    /// a credential with it.</param>
    /// <param name="complete">Whether that text is the whole definition of what it declares.</param>
    /// <param name="dependencies">Names of the objects this one depends on, as the source reported
    /// them.</param>
    /// <param name="notes">What the text leaves out, which the format requires whenever the text is a
    /// fragment.</param>
    /// <param name="capturedBy">Where the text came from: the engine function that reported it, or this
    /// pass, which synthesized it from catalog rows.</param>
    /// <remarks>
    /// No identity is set here. <see cref="DatabaseModelMapper"/> computes every id of a catalog from
    /// one graph, and an id worked out in two places is an id that can disagree with itself; a note
    /// therefore carries no id either, and says in words which object it means.
    /// </remarks>
    private NativeObjectDefinition Record(
        EObjectKind kind,
        string schemaName,
        string name,
        string? statement,
        bool complete = true,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<InterchangeIssue>? notes = null,
        string capturedBy = "synthesized") =>
        new()
        {
            Kind = kind,
            SchemaName = schemaName,
            Name = name,
            SourceObjectId = string.IsNullOrEmpty(schemaName) ? name : $"{schemaName}.{name}",
            Dependencies = dependencies,
            NativeDefinition = statement is null
                ? null
                : new NativeDefinition(_dialect, statement, complete, capturedBy),
            Notes = notes ?? [],
        };
}
