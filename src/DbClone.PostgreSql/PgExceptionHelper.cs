using Npgsql;

namespace DbClone.PostgreSql;

/// <summary>
/// Extracts clean, user-facing error messages from exceptions.
/// <para>
/// PostgreSQL exceptions (<see cref="PostgresException"/>) include a numeric SQLSTATE
/// code in their <see cref="Exception.Message"/> property — e.g. "42501: permission denied".
/// That code is useless noise for end users in the UI log.
/// </para>
/// <para>
/// This helper returns <see cref="PostgresException.MessageText"/> (the plain English
/// server message without the code) when the exception is a PostgreSQL error, and
/// falls back to the standard <see cref="Exception.Message"/> for everything else.
/// </para>
/// </summary>
internal static class PgExceptionHelper
{
    /// <summary>
    /// Returns a user-friendly error message suitable for UI display.
    /// </summary>
    /// <example>
    /// PostgresException  → "permission denied to create extension" (no "42501:" prefix)
    /// IOException        → ex.Message (unchanged, no SQLSTATE to strip)
    /// </example>
    public static string GetUserMessage(Exception ex)
    {
        if (ex is PostgresException postgresException)
            return postgresException.MessageText;

        return ex.Message;
    }
}
