using DbClone.PostgreSql.Execution;

using FluentAssertions;

namespace PostgreSql.Tests;

public class PgIdentifierQuoterTests
{
    [Fact]
    public void QuoteIdentifier_AlreadyQuoted_ReturnsAsIs()
    {
        PgIdentifierQuoter.QuoteIdentifier("\"already_quoted\"").Should().Be("\"already_quoted\"");
    }

    [Fact]
    public void QuoteIdentifier_ReservedWord_ReturnsQuoted()
    {
        PgIdentifierQuoter.QuoteIdentifier("select").Should().Be("\"select\"");
    }

    [Fact]
    public void QuoteIdentifier_SimpleLowercase_ReturnsBare()
    {
        PgIdentifierQuoter.QuoteIdentifier("my_table").Should().Be("my_table");
    }

    [Fact]
    public void QuoteIdentifier_StartsWithDigit_ReturnsQuoted()
    {
        PgIdentifierQuoter.QuoteIdentifier("1table").Should().Be("\"1table\"");
    }

    [Fact]
    public void QuoteIdentifier_WithSpaces_ReturnsQuoted()
    {
        PgIdentifierQuoter.QuoteIdentifier("my table").Should().Be("\"my table\"");
    }

    [Fact]
    public void QuoteIdentifier_WithUppercase_ReturnsQuoted()
    {
        PgIdentifierQuoter.QuoteIdentifier("MyTable").Should().Be("\"MyTable\"");
    }

    [Fact]
    public void QuoteSchemaQualified_ReturnsBothQuoted()
    {
        var result = PgIdentifierQuoter.QuoteSchemaQualified("public", "users");
        result.Should().Be("public.users");
    }

    [Fact]
    public void QuoteSchemaQualified_SpecialSchema_QuotesSchema()
    {
        var result = PgIdentifierQuoter.QuoteSchemaQualified("MySchema", "MyTable");
        result.Should().Be("\"MySchema\".\"MyTable\"");
    }

    [Fact]
    public void UnquoteIdentifier_NotQuoted_ReturnsAsIs()
    {
        PgIdentifierQuoter.UnquoteIdentifier("my_table").Should().Be("my_table");
    }

    [Fact]
    public void UnquoteIdentifier_Quoted_ReturnsBare()
    {
        PgIdentifierQuoter.UnquoteIdentifier("\"my_table\"").Should().Be("my_table");
    }

    [Fact]
    public void UnquoteIdentifier_WithEscapedQuotes_Unescapes()
    {
        PgIdentifierQuoter.UnquoteIdentifier("\"my\"\"table\"").Should().Be("my\"table");
    }
}
