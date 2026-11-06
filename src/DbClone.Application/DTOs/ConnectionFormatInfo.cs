namespace DbClone.Application.DTOs;

/// <summary>
/// The three values the export dialog shows for one connection-text format.
/// Declared here so the UI reads the engine's format list without naming a contract type.
/// </summary>
/// <param name="Id">Format identifier, e. g. "pg-uri".</param>
/// <param name="DisplayName">Human-readable name, e. g. "PostgreSQL URI".</param>
/// <param name="TypicalSource">Ecosystem the text usually comes from, e. g. "Java".</param>
public sealed record ConnectionFormatInfo(
    string Id,
    string DisplayName,
    string TypicalSource);
