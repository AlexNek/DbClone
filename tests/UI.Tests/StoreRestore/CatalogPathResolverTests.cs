using DbClone.Application.Interfaces;
using DbClone.UI.Models;
using DbClone.UI.StoreRestore.Services;
using DbClone.UI.ViewModels;

using FluentAssertions;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.StoreRestore;

/// <summary>
/// Unit tests for <see cref="CatalogPathResolver"/> folder naming and <see cref="CatalogNameSource"/>.
/// Pure name computation — no database, no dialog, no file-system access.
/// </summary>
public sealed class CatalogPathResolverTests
{
    private const string Root = @"C:\catalogs";

    /// <summary>The shape {timestamp} renders: yyyy-MM-dd_HH-mm.</summary>
    private const string TimestampPattern = @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}$";

    private const string ConnectionThenTimestampPattern = @"^Invoice_matcher_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}$";

    private const string BudgetedTokenThenTimestampPattern = @"^n{60}_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}$";

    private readonly CatalogPathResolver _resolver = new();

    private static CatalogNameSource Names(string databaseName = "testdb", string? connectionName = "Invoice matcher") =>
        new(databaseName, connectionName);

    private static string Folder(string resolved) => Path.GetFileName(resolved);

    // ── {Database} keeps its meaning ────────────────────────────────────────────────

    [Fact]
    public void Database_token_still_resolves_to_the_database_name()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Database}_{timestamp}", Names());

        Folder(result).Should().StartWith("testdb_");
    }

    [Fact]
    public void Database_token_is_used_by_a_pattern_saved_before_the_connection_token_existed()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Database}", Names());

        result.Should().Be(Path.Combine(Root, "testdb"));
    }

    // ── {Connection} ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{Connection}", "Invoice_matcher")]
    [InlineData("{connection}", "Invoice_matcher")]
    public void Connection_token_expands_to_the_saved_connection_name(string pattern, string expected)
    {
        var result = _resolver.ResolveCatalogPath(Root, pattern, Names());

        result.Should().Be(Path.Combine(Root, expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Connection_token_falls_back_to_the_database_name(string? connectionName)
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("neondb", connectionName));

        result.Should().Be(Path.Combine(Root, "neondb"));
    }

    [Fact]
    public void Default_pattern_names_the_folder_after_the_connection_and_keeps_the_timestamp()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}_{timestamp}", Names("neondb"));

        Folder(result).Should().MatchRegex(ConnectionThenTimestampPattern);
    }

    // ── Rule 1: whitespace ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A  B", "A_B")]
    [InlineData("A \t B", "A_B")]
    [InlineData("crm ", "crm")]
    [InlineData("  lead", "lead")]
    public void Every_run_of_whitespace_becomes_one_underscore_never_an_empty_gap(string connectionName, string expected)
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("testdb", connectionName));

        result.Should().Be(Path.Combine(Root, expected));
    }

    // ── Rule 2: characters a folder name cannot hold ────────────────────────────────

    [Theory]
    [InlineData("web/db:1", "web_db_1")]
    [InlineData("a*b?c", "a_b_c")]
    [InlineData("host|<x>", "host__x")]
    public void Invalid_characters_in_a_connection_name_become_underscores(string connectionName, string expected)
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("testdb", connectionName));

        result.Should().Be(Path.Combine(Root, expected));
    }

    [Fact]
    public void A_connection_name_carrying_a_path_escape_cannot_leave_the_store_root()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("testdb", @"..\escape"));

        result.Should().Be(Path.Combine(Root, "escape"));
        Path.GetPathRoot(result).Should().Be(Path.GetPathRoot(Root));
    }

    // ── Rule 5: names Windows reserves ──────────────────────────────────────────────

    [Theory]
    [InlineData("COM1", "_COM1")]
    [InlineData("con", "_con")]
    [InlineData("LPT1.txt", "_LPT1.txt")]
    [InlineData("NUL", "_NUL")]
    public void A_reserved_device_name_gets_an_underscore_prefix(string connectionName, string expected)
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("testdb", connectionName));

        result.Should().Be(Path.Combine(Root, expected));
    }

    [Fact]
    public void A_reserved_device_name_is_left_alone_once_the_pattern_adds_a_timestamp()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}_{timestamp}", Names("testdb", "COM1"));

        Folder(result).Should().StartWith("COM1_");
    }

    [Fact]
    public void A_name_the_ceiling_cut_reduces_to_a_reserved_device_name_still_gets_the_prefix()
    {
        var result = _resolver.ResolveCatalogPath(Root, "CON" + new string('_', 97) + "x", Names());

        result.Should().Be(Path.Combine(Root, "_CON"));
    }

    // ── Rule 4: length budget ───────────────────────────────────────────────────────

    [Fact]
    public void An_over_long_connection_name_is_cut_at_the_token_budget_and_keeps_its_timestamp_tail()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}_{timestamp}", Names("testdb", new string('n', 80)));

        Folder(result).Should().MatchRegex(BudgetedTokenThenTimestampPattern);
        Folder(result).Should().NotEndWith(".").And.NotEndWith("_");
    }

    [Fact]
    public void An_assembled_name_over_the_ceiling_is_cut_at_it()
    {
        var pattern = "{Connection}" + new string('q', 50);

        var result = _resolver.ResolveCatalogPath(Root, pattern, Names("testdb", new string('m', 200)));

        Folder(result).Should().Be(new string('m', 60) + new string('q', 40));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var name = new string('a', 59) + "\uD83D\uDE00";

        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("testdb", name));

        Folder(result).Should().Be(new string('a', 59));
        Folder(result).Should().NotContain("\uD83D");
    }

    [Fact]
    public void A_token_under_the_budget_keeps_the_edge_characters_the_assembled_name_holds_inside()
    {
        var underscored = _resolver.ResolveCatalogPath(Root, "{Database}_{timestamp}", Names("testdb_"));
        var dotted = _resolver.ResolveCatalogPath(Root, "{Connection}_backup", Names("testdb", "fake-project."));

        Folder(underscored).Should().StartWith("testdb__");
        Folder(dotted).Should().Be("fake-project._backup");
    }

    // ── Rule 6: nothing left after the rules ────────────────────────────────────────

    [Fact]
    public void A_name_reduced_to_nothing_keeps_the_other_part_of_the_pattern()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}_{timestamp}", Names("testdb", "***"));

        Folder(result).Should().MatchRegex(TimestampPattern);
        result.Should().NotBe(Root);
    }

    [Fact]
    public void A_name_reduced_to_nothing_with_nothing_else_in_the_pattern_writes_into_the_root()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{Connection}", Names("testdb", "***"));

        result.Should().Be(Root);
    }

    // ── Patterns without tokens ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_pattern_returns_the_base_directory_unchanged(string pattern)
    {
        _resolver.ResolveCatalogPath(Root, pattern, Names()).Should().Be(Root);
    }

    [Fact]
    public void A_pattern_without_a_token_is_used_verbatim()
    {
        var result = _resolver.ResolveCatalogPath(Root, "backup", Names());

        result.Should().Be(Path.Combine(Root, "backup"));
    }

    [Fact]
    public void Timestamp_token_renders_the_documented_format()
    {
        var result = _resolver.ResolveCatalogPath(Root, "{timestamp}", Names());

        Folder(result).Should().MatchRegex(TimestampPattern);
    }

    // ── CatalogNameSource ───────────────────────────────────────────────────────────

    [Fact]
    public void FromViewModel_maps_the_selected_saved_connections_name_and_its_database_name()
    {
        var source = CreatePanel();
        source.SelectedSavedConnection = new SavedConnection { Name = "Invoice matcher", DatabaseName = "testdb" };

        var names = CatalogNameSource.FromViewModel(source);

        names.DatabaseName.Should().Be("testdb");
        names.ConnectionToken.Should().Be("Invoice matcher");
    }

    [Fact]
    public void FromViewModel_tolerates_no_selection_and_falls_back_to_the_database_name()
    {
        var source = CreatePanel();
        source.DatabaseName = "neondb";
        source.SelectedSavedConnection = null;

        var names = CatalogNameSource.FromViewModel(source);

        names.ConnectionName.Should().BeNull();
        names.ConnectionToken.Should().Be("neondb");
    }

    private static ConnectionViewModel CreatePanel() =>
        new(Substitute.For<IConnectionTextService>(), Substitute.For<IServerAdminService>(), TestPlatformResolver.Create());
}
