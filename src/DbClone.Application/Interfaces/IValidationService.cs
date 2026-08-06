using DbClone.Application.DTOs;
using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

/// <summary>
/// Validates database state.
/// </summary>
public interface IValidationService
{
    /// <summary>
    /// Validates the database before or after copy.
    /// </summary>
    Task<ValidationResult> ValidateAsync(
        DatabaseModel expectedModel,
        CancellationToken cancellationToken = default);
}
