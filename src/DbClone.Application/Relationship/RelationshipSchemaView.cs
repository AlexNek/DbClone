namespace DbClone.Application.Relationship;

/// <summary>
/// One schema row in a relationship view, read by the schema filter chips.
/// </summary>
/// <param name="Schema">The schema name.</param>
/// <param name="TableCount">The number of captured tables in this schema.</param>
/// <param name="RelatedTableCount">
/// Of those, the number that are an endpoint of at least one edge in either direction — a captured
/// parent counts, and an edge to an outside table counts, because the incident edge is a captured
/// fact. A self reference counts its table once; an outside end never joins a schema row even when
/// its reference names one, because the catalog never confirmed that schema.
/// </param>
public sealed record RelationshipSchemaView(
    string Schema,
    int TableCount,
    int RelatedTableCount);
