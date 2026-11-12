namespace DbClone.Application.Relationship;

/// <summary>
/// The two things a relationship graph can be read from. The kind is the only input the document's
/// wording takes: a catalog states what it captured, a live database states what it holds.
/// </summary>
public enum EGraphOriginKind
{
    /// <summary>A stored catalog folder, read without a connection.</summary>
    Catalog,

    /// <summary>A connected database, read through the adapter's metadata role.</summary>
    LiveDatabase
}
