using DbClone.Application.Targets;

using Microsoft.Extensions.DependencyInjection;

namespace DbClone.Application.Interchange;

/// <summary>
/// Extension methods for registering interchange services.
/// </summary>
public static class InterchangeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application-level interchange wrapper (<see cref="IInterchangeService"/>).
    /// Requires <see cref="DatabaseTargetFactory"/> to be registered beforehand.
    /// </summary>
    public static IServiceCollection AddInterchangeServices(this IServiceCollection services)
    {
        services.AddSingleton<IInterchangeService, InterchangeService>();
        return services;
    }
}
