using DbClone.Application.Models;
using DbClone.Application.TableFilter;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Resolves a <see cref="TableSelectionSpec"/> against a <see cref="DatabaseModel"/>:
/// removes excluded tables together with their table-owned objects, strips
/// foreign keys that would dangle, and skips views whose dependencies include
/// an excluded table. All decisions are collected in a <see cref="TableFilterReport"/>
/// so callers can surface warnings.
/// </summary>
public interface ITableFilterApplier
{
    /// <summary>
    /// Applies the spec to the model and returns the filtered model plus a report.
    /// A null or inactive spec returns the original model unchanged with an empty report.
    /// </summary>
    TableFilterResult Apply(DatabaseModel model, TableSelectionSpec? spec);

    /// <summary>
    /// Converts a dot-joined "schema.name" metadata string to a <see cref="TableId"/>.
    /// Only used for metadata fields that are already dot-joined by the provider.
    /// </summary>
    TableId ParseQualified(string qualifiedName);
}
