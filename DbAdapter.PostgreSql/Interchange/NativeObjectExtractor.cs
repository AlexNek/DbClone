using CanonicalModel.Models;

using DbAdapter.Definitions.Interchange;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// Extracts native (dialect-specific) objects from a <see cref="DatabaseModel"/>.
/// These objects cannot be converted to neutral format and must be preserved as native SQL.
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
    public IReadOnlyList<NativeObjectDefinition> Extract(DatabaseModel model)
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

    private NativeObjectDefinition ExtractFunction(FunctionDefinition func)
    {
        // Reconstruct the native SQL from the definition
        // The Definition property contains the full CREATE FUNCTION/PROCEDURE statement
        return new NativeObjectDefinition(
            ObjectKind: func.IsProcedure ? "procedure" : "function",
            SchemaName: func.SchemaName,
            Name: func.Name,
            NativeSql: func.Definition,
            SourceDialect: _dialect,
            Dependencies: null); // TODO: Extract dependencies from Definition if needed
    }

    private NativeObjectDefinition ExtractTrigger(TriggerDefinition trigger)
    {
        // Reconstruct CREATE TRIGGER statement
        var sql = $@"CREATE TRIGGER {trigger.Name}
    {trigger.Timing} {string.Join(" OR ", trigger.Events)} ON {trigger.SchemaName}.{trigger.TableName}
    {(trigger.IsRowLevel ? "FOR EACH ROW" : "FOR EACH STATEMENT")}
    {(trigger.Condition is not null ? $"WHEN ({trigger.Condition})" : string.Empty)}
    EXECUTE FUNCTION {trigger.FunctionSchema}.{trigger.FunctionName}()";

        return new NativeObjectDefinition(
            ObjectKind: "trigger",
            SchemaName: trigger.SchemaName,
            Name: trigger.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: [$"{trigger.FunctionSchema}.{trigger.FunctionName}"]);
    }

    private NativeObjectDefinition ExtractView(ViewDefinition view)
    {
        // The Definition property contains the full CREATE VIEW statement
        return new NativeObjectDefinition(
            ObjectKind: "view",
            SchemaName: view.SchemaName,
            Name: view.Name,
            NativeSql: view.Definition,
            SourceDialect: _dialect,
            Dependencies: view.ReferencedRelations);
    }

    private NativeObjectDefinition ExtractMaterializedView(MaterializedViewDefinition matView)
    {
        // The Definition property contains the full CREATE MATERIALIZED VIEW statement
        return new NativeObjectDefinition(
            ObjectKind: "materializedView",
            SchemaName: matView.SchemaName,
            Name: matView.Name,
            NativeSql: matView.Definition,
            SourceDialect: _dialect,
            Dependencies: null);
    }

    private NativeObjectDefinition ExtractExtension(ExtensionDefinition ext)
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

        return new NativeObjectDefinition(
            ObjectKind: "extension",
            SchemaName: ext.SchemaName ?? string.Empty,
            Name: ext.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: null);
    }

    private NativeObjectDefinition ExtractPolicy(PolicyDefinition policy)
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

        return new NativeObjectDefinition(
            ObjectKind: "policy",
            SchemaName: policy.SchemaName,
            Name: policy.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: [$"{policy.SchemaName}.{policy.TableName}"]);
    }

    private NativeObjectDefinition ExtractPublication(PublicationDefinition pub)
    {
        var sql = $"CREATE PUBLICATION {pub.Name}";

        if (pub.IsForAllTables)
        {
            sql += " FOR ALL TABLES";
        }
        else if (pub.Tables is { Count: > 0 })
        {
            sql += $" FOR TABLE {string.Join(", ", pub.Tables)}";
        }

        return new NativeObjectDefinition(
            ObjectKind: "publication",
            SchemaName: string.Empty,
            Name: pub.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: pub.Tables?.ToList());
    }

    private NativeObjectDefinition ExtractSubscription(SubscriptionDefinition sub)
    {
        // Subscriptions are complex - preserve the full definition
        var sql = $"CREATE SUBSCRIPTION {sub.Name} CONNECTION '{sub.ConnectionString}' PUBLICATION {sub.PublicationName}";

        return new NativeObjectDefinition(
            ObjectKind: "subscription",
            SchemaName: string.Empty,
            Name: sub.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: null);
    }

    private NativeObjectDefinition ExtractSequence(SequenceDefinition seq)
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

        return new NativeObjectDefinition(
            ObjectKind: "sequence",
            SchemaName: seq.SchemaName,
            Name: seq.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: null);
    }

    private NativeObjectDefinition ExtractEnum(EnumDefinition enumDef)
    {
        var values = string.Join(", ", enumDef.Labels.Select(v => $"'{v}'"));
        var sql = $"CREATE TYPE {enumDef.SchemaName}.{enumDef.Name} AS ENUM ({values})";

        return new NativeObjectDefinition(
            ObjectKind: "customType",
            SchemaName: enumDef.SchemaName,
            Name: enumDef.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: null);
    }

    private NativeObjectDefinition ExtractDomain(DomainDefinition domain)
    {
        var sql = $"CREATE DOMAIN {domain.SchemaName}.{domain.Name} AS {domain.DataType}";

        if (!domain.IsNullable)
        {
            sql += " NOT NULL";
        }

        if (domain.DefaultValue is not null)
        {
            sql += $" DEFAULT {domain.DefaultValue}";
        }

        // TODO: Add constraints if present

        return new NativeObjectDefinition(
            ObjectKind: "domain",
            SchemaName: domain.SchemaName,
            Name: domain.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: null);
    }

    private NativeObjectDefinition ExtractCompositeType(CompositeTypeDefinition compType)
    {
        var columns = string.Join(",\n    ", compType.Attributes.Select(a => $"{a.Name} {a.DataType}"));
        var sql = $"CREATE TYPE {compType.SchemaName}.{compType.Name} AS (\n    {columns}\n)";

        return new NativeObjectDefinition(
            ObjectKind: "customType",
            SchemaName: compType.SchemaName,
            Name: compType.Name,
            NativeSql: sql,
            SourceDialect: _dialect,
            Dependencies: null);
    }
}
