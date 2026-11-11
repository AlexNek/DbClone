namespace DbClone.Application.Interchange;

/// <summary>
/// One captured check constraint: its name and the expression the catalog recorded verbatim. The
/// format records the expression's text and never parses it, so nothing here knows SQL.
/// </summary>
/// <param name="Name">The constraint's captured name, or an empty string when the catalog states none.</param>
/// <param name="Expression">The expression text as captured.</param>
public sealed record CatalogCheckConstraintView(
    string Name,
    string Expression);
