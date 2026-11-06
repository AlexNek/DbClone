namespace DbAdapter.Definitions;

/// <summary>
/// The one boundary between the application and a database engine: which roles exist
/// and how they are created. The adapter composes the roles and does no work itself.
/// </summary>
public interface IDatabaseAdapter
{
    /// <summary>Dialect, engine range and adapter version.</summary>
    AdapterInfo Info { get; }

    /// <summary>Human-readable engine name, e.g. "PostgreSQL".</summary>
    string ProviderName { get; }

    /// <summary>Connection-text role. Stateless, one per engine.</summary>
    IConnectionStringSyntax Syntax { get; }

    /// <summary>Platform role. Stateless, one per engine.</summary>
    IPlatformResolver Platform { get; }

    /// <summary>Metadata role for <paramref name="target"/>.</summary>
    IMetadataSource CreateMetadataSource(DatabaseTarget target);

    /// <summary>Administration role for <paramref name="target"/>.</summary>
    IDatabaseAdmin CreateAdmin(DatabaseTarget target);

    /// <summary>Cleaning role for <paramref name="target"/>.</summary>
    IDatabaseCleaner CreateCleaner(DatabaseTarget target);

    /// <summary>Comparison role. Both targets arrive on each call.</summary>
    ITableComparer CreateComparer();

    /// <summary>
    /// The adapter's engine-specific model comparers, appended to the host's provider-neutral
    /// comparers when a comparison runs. Returns only what the engine owns (e.g. its DDL comparer);
    /// the shared comparers stay registered by the host (refactors/09 P6).
    /// </summary>
    IReadOnlyList<IModelComparer> CreateModelComparers();

    /// <summary>Copy role. Both targets travel in the request.</summary>
    ICopySession CreateCopySession();
}
