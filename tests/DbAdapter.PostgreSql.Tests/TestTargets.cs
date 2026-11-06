using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// A target of the block's own dialect. What crosses the boundary is one complete
/// connection string, so the tests describe endpoints the same way the application does.
/// </summary>
internal static class TestTargets
{
    public static DatabaseTarget Local(
        string database = "testdb",
        string username = "test_user",
        string password = "fake_pw_123",
        string sslMode = "Prefer") =>
        new(
            "postgresql",
            $"Host=test.example.com;Port=5432;Database={database};Username={username};"
            + $"Password={password};SSL Mode={sslMode}");
}
