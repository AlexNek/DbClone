using CanonicalModel.DTOs;
using CanonicalModel.Enums;

namespace DbAdapter.Definitions;

/// <summary>
/// A self-describing connection-text format of one engine.
/// Each implementation knows how to detect, parse (import) and write (export)
/// one specific connection string syntax for one provider.
/// </summary>
public interface IConnectionTextFormat
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
    bool CanExport(ConnectionStringFields fields);

    /// <summary>Returns true if this format can parse the given text.</summary>
    bool CanImport(string text);

    /// <summary>Formats connection fields into this format's string representation.</summary>
    string Export(ConnectionStringFields fields);

    /// <summary>Parses raw text into the engine's connection fields.</summary>
    ConnectionStringFields Parse(string text);
}
