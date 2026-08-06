using DbClone.Application.Enums;
using DbClone.Application.Models;

namespace DbClone.Application.Interfaces;

/// <summary>
/// A self-describing connection string format plugin.
/// Each implementation knows how to detect, parse (import), and format (export)
/// one specific connection string syntax for one provider.
/// Formats are registered via DI and discovered as IEnumerable&lt;IConnectionFormat&gt;.
/// </summary>
public interface IConnectionFormat
{
    /// <summary>Lower values are checked first during import detection.</summary>
    int DetectionPriority { get; }

    /// <summary>Human-readable name (e.g. "PostgreSQL URI", "JDBC").</summary>
    string DisplayName { get; }

    /// <summary>Unique identifier (e.g. "pg-uri", "pg-jdbc").</summary>
    string Id { get; }

    /// <summary>The database provider this format belongs to.</summary>
    EDatabaseProvider Provider { get; }

    /// <summary>Typical source ecosystem (e.g. "Java", ".NET", "Python").</summary>
    string TypicalSource { get; }

    /// <summary>Returns true if this format can export the given connection.</summary>
    bool CanExport(DatabaseConnection connection);

    /// <summary>Returns true if this format can parse the given text.</summary>
    bool CanImport(string text);

    /// <summary>Formats a connection model into this format's string representation.</summary>
    string Export(DatabaseConnection connection);

    /// <summary>Parses raw text into a provider-neutral connection model.</summary>
    DatabaseConnection Parse(string text);
}
