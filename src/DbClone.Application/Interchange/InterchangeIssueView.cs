namespace DbClone.Application.Interchange;

/// <summary>
/// A single issue reported by the interchange product during store or restore.
/// The application maps product issues to this type verbatim — it invents none and hides none.
/// </summary>
/// <param name="Severity">How serious the issue is.</param>
/// <param name="Code">Machine-readable issue code from the product.</param>
/// <param name="Sentence">One human-readable sentence describing the issue.</param>
/// <param name="Location">Where the issue applies (table name, object name, file path, or empty).</param>
/// <param name="Details">Extended detail text, or empty when the sentence is sufficient.</param>
public sealed record InterchangeIssueView(
    EInterchangeSeverity Severity,
    string Code,
    string Sentence,
    string Location,
    string Details);
