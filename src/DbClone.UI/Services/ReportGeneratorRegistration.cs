using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

namespace DbClone.UI.Services;

internal static class ReportGeneratorRegistration
{
    /// <summary>
    /// Scans the current assembly for all classes implementing <see cref="IReportGenerationService"/>
    /// and registers them as singletons. Adding a new report format requires only creating the class.
    /// </summary>
    public static IServiceCollection AddReportGenerators(this IServiceCollection services)
    {
        var reportInterface = typeof(IReportGenerationService);

        var implementations = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t =>
                t is { IsClass: true, IsAbstract: false } && reportInterface.IsAssignableFrom(t));

        foreach (var impl in implementations)
        {
            services.AddSingleton(reportInterface, impl);
        }

        return services;
    }
}
