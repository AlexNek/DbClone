namespace DbClone.Application.Relationship;

/// <summary>
/// A table a constraint names that the origin does not hold. It carries only the name the
/// constraint states — no rows, no columns, no indexes, no definition, because the origin holds
/// none of them, and no count field exists here so the renderer cannot print a number it lacks.
/// </summary>
/// <param name="Id">
/// The reference's own <c>Value</c> when it states something, so two identical references converge
/// on one node and two different ones never merge; a stated-unknown key when it states nothing.
/// </param>
/// <param name="Schema">The schema the reference states, or <see langword="null"/> when it states none.</param>
/// <param name="Table">The table name the reference states, or <see langword="null"/> when it states none.</param>
public sealed record RelationshipOutsideTableView(
    string Id,
    string? Schema,
    string? Table);
