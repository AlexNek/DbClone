using DbClone.Application.DTOs;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Represents a database provider capable of supplying all components needed for a copy operation.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>
    /// Gets the provider name (e.g., "PostgreSQL").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Creates a capability detector.
    /// </summary>
    ICapabilityDetector CreateCapabilityDetector(ConnectionInfo connection);

    /// <summary>
    /// Creates a data copier.
    /// </summary>
    IDataCopier CreateDataCopier(ConnectionInfo source, ConnectionInfo destination);

    /// <summary>
    /// Creates a DDL generator.
    /// </summary>
    IDdlGenerator CreateDdlGenerator();

    /// <summary>
    /// Creates a dependency analyzer.
    /// </summary>
    IDependencyAnalyzer CreateDependencyAnalyzer();

    /// <summary>
    /// Creates a metadata reader for the given connection.
    /// </summary>
    IMetadataReader CreateMetadataReader(ConnectionInfo connection);

    /// <summary>
    /// Creates a validation service.
    /// </summary>
    IValidationService CreateValidationService(ConnectionInfo connection);
}
