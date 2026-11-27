using DbAdapter.PostgreSql.Metadata;

using FluentAssertions;

using System.Data;
using System.Data.Common;

using CanonicalModel.Enums;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Pins the two rules the reader decides without a database: which schema belongs to a managed
/// platform, and which settings a function's <c>SET</c> clauses name. Answering the first wrongly
/// hides a schema the user owns from every read that leaves platform schemas out — including the
/// check a restore runs on its own work; answering the second wrongly hands the copy, or an
/// administrator, the wrong parameter to grant.
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

    [Fact]
    public void Parse_OneEntryPerSetting_KeepsOnlyTheName()
    {
        // proconfig holds "name=value" per SET clause; the value can hold a path or an
        // authorization, so only the name travels into the model.
        PgFunctionSettings.Parse(["log_min_messages=warning", "search_path=public, extensions"])
            .Should().Equal("log_min_messages", "search_path");
    }

    [Fact]
    public void Parse_FunctionWithoutASetClause_IsEmpty()
    {
        PgFunctionSettings.Parse(null).Should().BeEmpty();
        PgFunctionSettings.Parse([]).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ValueContainingAnEquals_SplitsAtTheFirstOne()
    {
        PgFunctionSettings.Parse(["search_path=public, custom=1"]).Should().Equal("search_path");
    }

    [Fact]
    public void MapFunction_RowWithSetClauses_CarriesTheirNamesWithTheDefinition()
    {
        // The column order is the query's, and proconfig is read by its position: reading the wrong
        // position would leave every function looking as if it asked for nothing, and the copy would
        // then create it only to be refused by the server.
        using var reader = FunctionRow(["log_min_messages=warning", "search_path=public"]);
        reader.Read().Should().BeTrue();

        var function = PgMetadataReader.MapFunction(reader);

        function.SchemaName.Should().Be("realtime");
        function.Name.Should().Be("list_changes");
        function.IsProcedure.Should().BeFalse();
        function.Comment.Should().BeNull();
        function.Volatility.Should().Be(EFunctionVolatility.Stable);
        function.SetParameters.Should().Equal("log_min_messages", "search_path");
    }

    [Fact]
    public void MapFunction_RowWithoutProconfig_CarriesNoParameter()
    {
        using var reader = FunctionRow(setParameters: null);
        reader.Read().Should().BeTrue();

        PgMetadataReader.MapFunction(reader).SetParameters.Should().BeEmpty();
    }

    /// <summary>
    /// One function row carrying the twelve columns the function query selects, in its order.
    /// </summary>
    private static DbDataReader FunctionRow(string[]? setParameters)
    {
        var table = new DataTable();
        table.Columns.Add("schema", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("language", typeof(string));
        table.Columns.Add("return_type", typeof(string));
        table.Columns.Add("definition", typeof(string));
        table.Columns.Add("volatility", typeof(string));
        table.Columns.Add("is_strict", typeof(bool));
        table.Columns.Add("security_definer", typeof(bool));
        table.Columns.Add("comment", typeof(string));
        table.Columns.Add("is_procedure", typeof(bool));
        table.Columns.Add("arguments", typeof(string));
        table.Columns.Add("set_parameters", typeof(object));

        var row = table.NewRow();
        row["schema"] = "realtime";
        row["name"] = "list_changes";
        row["language"] = "plpgsql";
        row["return_type"] = "void";
        row["definition"] = "CREATE FUNCTION realtime.list_changes() RETURNS void AS $$ BEGIN END $$ LANGUAGE plpgsql";
        row["volatility"] = "s";
        row["is_strict"] = false;
        row["security_definer"] = false;
        row["comment"] = DBNull.Value;
        row["is_procedure"] = false;
        row["arguments"] = string.Empty;
        row["set_parameters"] = setParameters is null ? DBNull.Value : setParameters;
        table.Rows.Add(row);

        return table.CreateDataReader();
    }
}
