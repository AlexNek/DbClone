using System.Globalization;

using DbClone.Application.DTOs;
using DbClone.Application.Interchange;
using DbClone.Application.Targets;

using Microsoft.Extensions.Logging;

namespace DbClone.Application.Relationship;

/// <summary>
/// Reads a relationship graph from the origin the request names: the interchange product for a catalog
/// folder, the adapter's metadata role for a live database.
/// </summary>
/// <remarks>
/// The live read is the same one Compare performs for its destination — resolve the adapter from the
/// connection's engine, create the metadata source for the target, read the model — with
/// <c>excludePlatformSchemas: false</c> so a view of a database and the catalog stored from it list the
/// same tables.
/// </remarks>
public sealed class RelationshipGraphService : IRelationshipGraphService
{
    private readonly IInterchangeService _interchange;

    private readonly DatabaseTargetFactory _targetFactory;

    private readonly ILogger<RelationshipGraphService> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="interchange">The catalog reader the view delegates to.</param>
    /// <param name="targetFactory">The boundary that resolves the adapter and builds the target.</param>
    /// <param name="logger">The application's logger.</param>
    public RelationshipGraphService(
        IInterchangeService interchange,
        DatabaseTargetFactory targetFactory,
        ILogger<RelationshipGraphService> logger)
    {
        _interchange = interchange ?? throw new ArgumentNullException(nameof(interchange));
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<RelationshipGraphView> ReadCatalogAsync(string catalogPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        // One deserialization path: the product already knows the catalog's layout, and a second parser
        // here would be a second thing to keep in step.
        return _interchange.ReadGraphAsync(catalogPath, ct);
    }

    /// <inheritdoc />
    public async Task<RelationshipGraphView> ReadDatabaseAsync(
        ConnectionInfo connection,
        ERelationshipRole role,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var adapter = _targetFactory.ResolveAdapter(connection.Engine);
        var target = _targetFactory.Create(connection);

        // The log names the side, the host and the database — never the user and never the password.
        _logger.LogInformation(
            "Relationship view: reading {Role} {Host}:{Port}/{Database}",
            role,
            connection.Host,
            connection.Port,
            connection.DatabaseName);

        var metadata = adapter.CreateMetadataSource(target);
        var model = await metadata
            .ReadDatabaseModelAsync(excludePlatformSchemas: false, ct: ct)
            .ConfigureAwait(false);

        var origin = new GraphViewOrigin(
            Kind: EGraphOriginKind.LiveDatabase,
            Label: connection.DatabaseName,
            Location: string.Create(
                CultureInfo.InvariantCulture,
                $"{connection.Host}:{connection.Port}/{connection.DatabaseName}"),
            Dialect: adapter.Info.Dialect,
            FormatVersion: null);

        return DatabaseModelGraphMapper.Map(model, origin);
    }
}
