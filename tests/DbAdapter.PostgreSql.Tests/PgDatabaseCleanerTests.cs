using DbAdapter.PostgreSql.Providers;

using FluentAssertions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Pins the rule that a cleaned target really is the empty target a restore accepts, without a
/// database: every routine the emptiness check counts must be dropped with the statement its kind
/// needs, because one verb for all kinds leaves the target holding what the clean reported gone.
/// </summary>
public class PgDatabaseCleanerTests
{
    [Fact]
    public void RoutineDropStatement_DropsAProcedureWithDropProcedure()
    {
        // A procedure answers "DROP FUNCTION" with an error, and the routines used to be removed by
        // one statement — so a single procedure ended the drop and every routine behind it survived,
        // in a database the user had just asked to overwrite and that the restore then refused.
        var sql = PgDatabaseCleaner.RoutineDropStatement("public.run_report()", "p");

        sql.Should().Be("DROP PROCEDURE IF EXISTS public.run_report() CASCADE");
    }

    [Theory]
    [InlineData("f", "public.count_rows()", "DROP FUNCTION")]
    [InlineData("w", "public.rank_rows()", "DROP FUNCTION")]
    [InlineData("a", "public.total(integer)", "DROP AGGREGATE")]
    public void RoutineDropStatement_UsesTheOtherVerbsItsKindsRequire(
        string prokind, string identity, string expectedVerb)
    {
        // Functions and window functions are both functions; an aggregate is counted by the emptiness
        // check too, and "DROP FUNCTION" cannot take one.
        var sql = PgDatabaseCleaner.RoutineDropStatement(identity, prokind);

        sql.Should().Be($"{expectedVerb} IF EXISTS {identity} CASCADE");
    }
}
