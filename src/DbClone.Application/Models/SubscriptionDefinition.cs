namespace DbClone.Application.Models;

/// <summary>
/// Represents a subscription.
/// </summary>
public sealed record SubscriptionDefinition(
    string Name,
    string ConnectionString,
    string PublicationName,
    bool IsEnabled,
    string? Comment);
