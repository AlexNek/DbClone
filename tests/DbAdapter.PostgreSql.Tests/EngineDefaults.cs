namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The engine default port the adapter composes into its formats — the value its platform
/// data declares (postgresql.platform → defaults.port). Formats receive it rather than
/// carrying a literal, so a test that hands them a different number must see that number
/// come back.
/// </summary>
internal static class EngineDefaults
{
    public const int DefaultPort = 5432;
}
