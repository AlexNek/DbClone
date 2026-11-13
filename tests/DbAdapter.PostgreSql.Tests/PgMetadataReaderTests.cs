using DbAdapter.PostgreSql.Metadata;

using FluentAssertions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Pins the rule that decides which schema belongs to a managed platform, without a database:
/// answering it wrongly hides a schema the user owns from every read that leaves platform schemas
/// out — including the check a restore runs on its own work.
/// </summary>
public class PgMetadataReaderTests
{
    [Fact]
    public void IsPlatformManaged_TheRoleThatOwnsPublicFromPostgres15_IsTheDatabasesOwn()
    {
        // From PostgreSQL 15 public belongs to pg_database_owner, a built-in role that cannot log in
        // and has no members a membership check can name. Reading it as somebody else's hid public —
        // and every table in it — so a correct restore reported a target holding no schema and no
        // table over the rows it had just written.
        var owners = new HashSet<string>(["test_user"], StringComparer.OrdinalIgnoreCase);

        PgMetadataReader.IsPlatformManaged("pg_database_owner", hasUsage: true, owners)
            .Should().BeFalse();
    }

    [Fact]
    public void IsPlatformManaged_AnOwnerTheUserIsPartOf_IsTheUsersOwn()
    {
        var owners = new HashSet<string>(["test_user"], StringComparer.OrdinalIgnoreCase);

        PgMetadataReader.IsPlatformManaged("test_user", hasUsage: true, owners).Should().BeFalse();
    }

    [Fact]
    public void IsPlatformManaged_AnOwnerTheUserIsNotPartOf_IsAPlatform()
    {
        var owners = new HashSet<string>(["test_user"], StringComparer.OrdinalIgnoreCase);

        PgMetadataReader.IsPlatformManaged("platform_admin", hasUsage: true, owners)
            .Should().BeTrue();
    }

    [Fact]
    public void IsPlatformManaged_ASchemaTheUserMayNotUse_IsLeftOutWhateverOwnsIt()
    {
        // Without USAGE nothing in it can be read or written, so it is not content the user holds.
        var owners = new HashSet<string>(["test_user"], StringComparer.OrdinalIgnoreCase);

        PgMetadataReader.IsPlatformManaged("test_user", hasUsage: false, owners).Should().BeTrue();
    }
}
