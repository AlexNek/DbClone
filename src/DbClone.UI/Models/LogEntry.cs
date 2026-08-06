using DbClone.Application.Enums;

namespace DbClone.UI.Models;

/// <summary>
/// A single structured entry in the runtime log pane.
/// Severity is carried by <see cref="Level"/> — display text never encodes severity.
/// </summary>
/// <param name="Level">Severity level of the entry.</param>
/// <param name="Message">Human-readable message text.</param>
/// <param name="Timestamp">Entry timestamp; null for continuation/detail lines.</param>
public sealed record LogEntry(ELogLevel Level, string Message, DateTime? Timestamp = null)
{
    /// <summary>Formatted text for display and clipboard export.</summary>
    public string Display => Timestamp is { } ts ? $"[{ts:HH:mm:ss}] {Message}" : Message;

    public override string ToString() => Display;
}
