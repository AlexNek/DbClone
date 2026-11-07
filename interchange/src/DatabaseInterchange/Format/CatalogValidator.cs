using CanonicalModel.Interchange;

namespace DatabaseInterchange.Format;

/// <summary>
/// Validates a snapshot model before writing to a catalog.
/// </summary>
public sealed class CatalogValidator
{
    /// <summary>
    /// Validates a snapshot before writing.
    /// </summary>
    /// <param name="snapshot">The snapshot to validate.</param>
    /// <returns>A validation result.</returns>
    public CatalogValidationResult Validate(InterchangeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var issues = new List<string>();

        // Validate database
        if (snapshot.Database is null)
        {
            issues.Add("Snapshot must have a Database definition.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(snapshot.Database.Name))
            {
                issues.Add("Database must have a name.");
            }
        }

        // Validate schemas
        var schemaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in snapshot.Schemas)
        {
            if (string.IsNullOrWhiteSpace(schema.Name))
            {
                issues.Add("Schema must have a name.");
            }
            else if (!schemaNames.Add(schema.Name))
            {
                issues.Add($"Duplicate schema name: {schema.Name}");
            }
        }

        // Validate tables
        var tableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in snapshot.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Id.Hex))
            {
                issues.Add($"Table '{table.Name}' must have an Id.");
            }
            else if (!tableIds.Add(table.Id.Hex))
            {
                issues.Add($"Duplicate table Id: {table.Id.Hex}");
            }

            if (string.IsNullOrWhiteSpace(table.Name))
            {
                issues.Add("Table must have a name.");
            }

            // Validate columns
            if (table.Columns.Count == 0)
            {
                issues.Add($"Table '{table.Name}' must have at least one column.");
            }

            var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                if (string.IsNullOrWhiteSpace(column.Name))
                {
                    issues.Add($"Table '{table.Name}' has a column without a name.");
                }
                else if (!columnNames.Add(column.Name))
                {
                    issues.Add($"Table '{table.Name}' has duplicate column: {column.Name}");
                }
            }

            // Validate primary key
            if (table.PrimaryKey is not null)
            {
                foreach (var pkColumn in table.PrimaryKey.Columns)
                {
                    if (!table.Columns.Any(c => c.Name == pkColumn))
                    {
                        issues.Add($"Table '{table.Name}' primary key references unknown column: {pkColumn}");
                    }
                }
            }

            // Validate foreign keys
            foreach (var fk in table.ForeignKeys)
            {
                foreach (var fkColumn in fk.Columns)
                {
                    if (!table.Columns.Any(c => c.Name == fkColumn))
                    {
                        issues.Add($"Table '{table.Name}' foreign key references unknown column: {fkColumn}");
                    }
                }
            }

            // Validate indexes
            foreach (var index in table.Indexes)
            {
                if (string.IsNullOrWhiteSpace(index.Name))
                {
                    issues.Add($"Table '{table.Name}' has an index without a name.");
                }
            }
        }

        // Validate views
        foreach (var view in snapshot.Views)
        {
            if (string.IsNullOrWhiteSpace(view.Id.Hex))
            {
                issues.Add($"View '{view.Name}' must have an Id.");
            }

            if (string.IsNullOrWhiteSpace(view.Name))
            {
                issues.Add("View must have a name.");
            }
        }

        // Validate routines
        foreach (var routine in snapshot.Routines)
        {
            if (string.IsNullOrWhiteSpace(routine.Id.Hex))
            {
                issues.Add($"Routine '{routine.Name}' must have an Id.");
            }

            if (string.IsNullOrWhiteSpace(routine.Name))
            {
                issues.Add("Routine must have a name.");
            }

            // Validate implementations
            if (routine.Implementations.Count == 0)
            {
                issues.Add($"Routine '{routine.Name}' must have at least one implementation.");
            }

            var dialects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var impl in routine.Implementations)
            {
                if (string.IsNullOrWhiteSpace(impl.Dialect))
                {
                    issues.Add($"Routine '{routine.Name}' has an implementation without a dialect.");
                }
                else if (!dialects.Add(impl.Dialect))
                {
                    issues.Add($"Routine '{routine.Name}' has duplicate implementation for dialect: {impl.Dialect}");
                }

                if (string.IsNullOrWhiteSpace(impl.Body))
                {
                    issues.Add($"Routine '{routine.Name}' implementation for '{impl.Dialect}' has no body.");
                }
            }
        }

        // Validate sequences
        foreach (var sequence in snapshot.Sequences)
        {
            if (string.IsNullOrWhiteSpace(sequence.Id.Hex))
            {
                issues.Add($"Sequence '{sequence.Name}' must have an Id.");
            }

            if (string.IsNullOrWhiteSpace(sequence.Name))
            {
                issues.Add("Sequence must have a name.");
            }
        }

        // Validate extensions
        foreach (var extension in snapshot.Extensions)
        {
            if (string.IsNullOrWhiteSpace(extension.Name))
            {
                issues.Add("Extension must have a name.");
            }
        }

        return new CatalogValidationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
        };
    }
}

/// <summary>
/// Result of catalog validation.
/// </summary>
public sealed record CatalogValidationResult
{
    /// <summary>Gets whether the catalog is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Gets the validation issues.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}
