namespace DbClone.Application.Models;

/// <summary>
/// Represents an extension.
/// </summary>
public sealed record ExtensionDefinition(
    string Name,
    string SchemaName,
    string Version,
    string? Comment);
