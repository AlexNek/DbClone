using DbClone.PostgreSql.Copy;
using DbClone.PostgreSql.Providers;

using FluentAssertions;

namespace PostgreSql.Tests;

/// <summary>
/// Pins the SQL semantics that make legacy-inheritance copies correct, without a database:
/// the data copier must read a table's OWN rows only (FROM ONLY), while the comparison
/// provider must count inheritance-visible rows (no ONLY) so a dropped INHERITS
/// relationship surfaces as a mismatch.
/// </summary>
public class CopySqlGenerationTests
{
    [Fact]
    public void DataCopier_RowCountSql_ReadsOnlyOwnRows()
    {
        // A legacy-inheritance parent read without ONLY would also return its children's
        // rows; those are copied separately when the child is processed — double-copy.
        var sql = PgDataCopier.BuildRowCountSql("public.parent_t");

        sql.Should().Be("SELECT count(*) FROM ONLY public.parent_t");
    }

    [Fact]
    public void DataCopier_SelectSql_ReadsOnlyOwnRows()
    {
        var sql = PgDataCopier.BuildSelectSql("id, name", "public.parent_t");

        sql.Should().Be("SELECT id, name FROM ONLY public.parent_t");
    }

    [Fact]
    public void TableComparer_CountSql_DeliberatelyOmitsOnly()
    {
        // Comparison must see the same inheritance-visible row set on both sides:
        // the source parent counts its children's rows, so the destination parent must
        // too. Counting with ONLY here would hide a lost INHERITS relationship.
        var sql = PgTableComparerProvider.BuildCountSql("\"public\".\"parent_t\"");

        sql.Should().Be("SELECT count(*) FROM \"public\".\"parent_t\"");
        sql.Should().NotContain("ONLY");
    }
}
