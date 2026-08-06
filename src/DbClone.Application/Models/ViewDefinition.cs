namespace DbClone.Application.Models;

/// <summary>
/// Represents a view definition.
/// </summary>
/// <param name="SchemaName">Schema that contains the view.</param>
/// <param name="Name">View name.</param>
/// <param name="Definition">SQL definition text of the view.</param>
/// <param name="Comment">Optional COMMENT annotation on the view.</param>
/// <param name="ReferencedRelations">
/// Schema-qualified names ("schema.name") of the relations this view reads from,
/// obtained from pg_depend. Used to order dependent views during creation.
/// </param>
public sealed record ViewDefinition(
    string SchemaName,
    string Name,
    string Definition,
    string? Comment,
    IReadOnlyList<string>? ReferencedRelations = null);
