using DbClone.UI.ViewModels;

namespace DbClone.UI.StoreRestore.Services;

/// <summary>
/// The names a catalog folder can be built from.
/// Single responsibility: carry both naming sources so the resolver decides which token gets which.
/// </summary>
/// <param name="DatabaseName">The engine database name the connection points at — what <c>{Database}</c> means.</param>
/// <param name="ConnectionName">The display name of the saved connection selected on the panel, or null when none is.</param>
public sealed record CatalogNameSource(string DatabaseName, string? ConnectionName)
{
    /// <summary>
    /// The value <c>{Connection}</c> expands to: the selected saved connection's display name,
    /// falling back to the database name when no saved connection is in play or its name is blank.
    /// </summary>
    public string ConnectionToken =>
        string.IsNullOrWhiteSpace(ConnectionName) ? DatabaseName : ConnectionName!;

    /// <summary>Reads both names from a connection panel.</summary>
    public static CatalogNameSource FromViewModel(ConnectionViewModel source) =>
        new(source.DatabaseName, source.SelectedSavedConnection?.Name);
}
