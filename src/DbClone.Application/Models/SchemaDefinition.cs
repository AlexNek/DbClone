namespace DbClone.Application.Models;

/// <summary>
/// Represents a database schema.
/// </summary>
/// <param name="Name">The schema name.</param>
/// <param name="Owner">The schema owner.</param>
/// <param name="Comment">Optional comment.</param>
/// <param name="IsSystem">
/// True for engine-managed system schemas (e.g. pg_catalog, information_schema).
/// System schemas are never created or copied, and only their presence is compared.
/// </param>
public sealed record SchemaDefinition(
    string Name,
    string Owner,
    string? Comment = null,
    bool IsSystem = false);
