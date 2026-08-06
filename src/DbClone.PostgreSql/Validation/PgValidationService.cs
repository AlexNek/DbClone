using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.PostgreSql.Execution;

using Microsoft.Extensions.Logging;

namespace DbClone.PostgreSql.Validation;

/// <summary>
/// PostgreSQL implementation of <see cref="IValidationService"/>.
/// Validates destination database against expected model.
/// </summary>
public sealed class PgValidationService : IValidationService
{
    private readonly PgSqlExecutor _executor;

    private readonly ILogger<PgValidationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgValidationService"/> class.
    /// </summary>
    public PgValidationService(PgSqlExecutor executor, ILogger<PgValidationService> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ValidationResult> ValidateAsync(
        DatabaseModel expectedModel,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Validating destination database");

        var issues = new List<ValidationIssue>();

        // Validate table count
        var actualTableCount = await _executor.ExecuteScalarAsync<long>(
                                   "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                                   +
                                   $"WHERE c.relkind IN ({PgRelKind.TableOrPartition}) AND n.nspname NOT IN ({PgSystemSchemas.SqlList})",
                                   cancellationToken);

        if (actualTableCount != expectedModel.Tables.Count)
        {
            issues.Add(
                new ValidationIssue(
                    EValidationSeverity.Warning,
                    $"Table count mismatch: expected {expectedModel.Tables.Count}, found {actualTableCount}",
                    null));
        }

        // Validate row counts for each table
        foreach (var table in expectedModel.Tables)
        {
            var qualifiedName =
                PgIdentifierQuoter.QuoteSchemaQualified(table.SchemaName, table.Name);
            try
            {
                var rowCount = await _executor.ExecuteScalarAsync<long>(
                                   $"SELECT count(*) FROM {qualifiedName}",
                                   cancellationToken);
                issues.Add(
                    new ValidationIssue(
                        EValidationSeverity.Info,
                        $"{qualifiedName}: {rowCount} rows",
                        qualifiedName));
            }
            catch (Exception ex)
            {
                issues.Add(
                    new ValidationIssue(
                        EValidationSeverity.Error,
                        $"Could not validate {qualifiedName}: {PgExceptionHelper.GetUserMessage(ex)}",
                        qualifiedName));
            }
        }

        // Validate sequence count
        var actualSeqCount = await _executor.ExecuteScalarAsync<long>(
                                 "SELECT count(*) FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid "
                                 +
                                 "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                                 $"WHERE n.nspname NOT IN ({PgSystemSchemas.SqlList})",
                                 cancellationToken);

        if (actualSeqCount != expectedModel.Sequences.Count)
        {
            issues.Add(
                new ValidationIssue(
                    EValidationSeverity.Warning,
                    $"Sequence count mismatch: expected {expectedModel.Sequences.Count}, found {actualSeqCount}",
                    null));
        }

        // Validate index count
        var actualIndexCount = await _executor.ExecuteScalarAsync<long>(
                                   "SELECT count(*) FROM pg_index i " +
                                   "JOIN pg_class t ON t.oid = i.indrelid " +
                                   "JOIN pg_namespace n ON n.oid = t.relnamespace " +
                                   $"WHERE n.nspname NOT IN ({PgSystemSchemas.SqlList}) " +
                                   $"AND t.relkind IN ({PgRelKind.TableOrPartition})",
                                   cancellationToken);

        var expectedIndexCount = expectedModel.Tables.Sum(t => t.Indexes.Count);
        if (actualIndexCount != expectedIndexCount)
        {
            issues.Add(
                new ValidationIssue(
                    EValidationSeverity.Info,
                    $"Index count: expected {expectedIndexCount}, found {actualIndexCount}",
                    null));
        }

        var isValid = !issues.Any(i => i.Severity == EValidationSeverity.Error);

        _logger.LogInformation(
            "Validation complete: {Valid}, {IssueCount} issues ({ErrorCount} errors)",
            isValid ? "PASS" : "FAIL",
            issues.Count,
            issues.Count(i => i.Severity == EValidationSeverity.Error));

        return new ValidationResult(isValid, issues);
    }
}
