using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Pipeline;

/// <summary>
/// The foreign keys of one table that the copy may create. The owning table travels with
/// them because a <see cref="ForeignKeyDefinition"/> names only the side it references,
/// while the statement that creates it needs the child's schema and name.
/// </summary>
/// <param name="Table">The child table the constraints are stored with.</param>
/// <param name="ForeignKeys">The constraints whose parent table is inside the copied scope.</param>
public sealed record InScopeForeignKeys(TableId Table, IReadOnlyList<ForeignKeyDefinition> ForeignKeys);
