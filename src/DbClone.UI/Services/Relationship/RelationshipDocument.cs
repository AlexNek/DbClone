using DbClone.Application.Relationship;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// One relationship document, held in memory: the origin it describes and the HTML that describes it.
/// Nothing here names a path, because a view is not a file (feature 11, D1).
/// </summary>
/// <param name="Origin">Where the graph was read from; the title and the header state it.</param>
/// <param name="Html">The whole document, as text.</param>
internal sealed record RelationshipDocument(GraphViewOrigin Origin, string Html);
