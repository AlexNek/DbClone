using DbClone.Application.DTOs;

namespace DbClone.Application.Relationship;

/// <summary>
/// The one way a relationship graph is read: from a stored catalog folder, or from a connected
/// database. Both return the same origin-neutral view, so one renderer serves either.
/// </summary>
/// <remarks>
/// This service reads; it never writes a document, a file or a cache entry. Where the picture is shown
/// is the presentation layer's decision, and the catalog's own store-time picture stays the store's
/// artefact (feature 11, D1).
/// </remarks>
public interface IRelationshipGraphService
{
    /// <summary>Reads the relationship graph of a stored catalog folder.</summary>
    /// <param name="catalogPath">The catalog folder.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The catalog's graph, stating a catalog origin.</returns>
    Task<RelationshipGraphView> ReadCatalogAsync(string catalogPath, CancellationToken ct);

    /// <summary>
    /// Reads the relationship graph of a live database: metadata only, no store, no export, no write to
    /// the database and no row count.
    /// </summary>
    /// <param name="connection">The connection to read: its host, port and database name reach the
    /// origin; its user and password are never read into the view.</param>
    /// <param name="role">Which panel asked, for the log line only.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The database's graph, stating a live origin.</returns>
    Task<RelationshipGraphView> ReadDatabaseAsync(
        ConnectionInfo connection,
        ERelationshipRole role,
        CancellationToken ct);
}
