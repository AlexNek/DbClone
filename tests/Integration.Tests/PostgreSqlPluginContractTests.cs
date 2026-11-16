using CanonicalModel.DTOs;
using CanonicalModel.Enums;

using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Adapters;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace Integration.Tests;

/// <summary>
/// The two halves of the plugin boundary, joined for real: the engine block's own adapter, and the
/// values it reports to the host. No database is opened — what is under test is the contract the
/// host discovers, not the server behind it.
/// </summary>
public sealed class PostgreSqlPluginContractTests
{
    [Fact]
    public void AdapterAnnouncesADialectAndTheRolesTheHostAsksFor()
    {
        // Arrange — the plugin directory is the build output, where the .platform files land.
        var adapter = CreateAdapter();

        // Act
        var modelComparers = adapter.CreateModelComparers();

        // Assert
        adapter.Info.Id.Should().NotBeNullOrWhiteSpace();
        adapter.Info.Dialect.Should().Be("postgresql");
        adapter.Info.EngineVersionRange.Should().NotBeNullOrWhiteSpace(
            "the copy workflow refuses an unsupported server from this value");
        adapter.CreateAdmin(Target()).Should().NotBeNull();
        adapter.CreateCleaner(Target()).Should().NotBeNull();
        adapter.CreateComparer().Should().NotBeNull();
        adapter.CreateCopySession().Should().NotBeNull();
        adapter.CreateScriptRenderer().Should().NotBeNull();
        modelComparers.Should().NotBeEmpty();
    }

    [Fact]
    public void AdapterReportsTheFormatsAndPlatformsItOwns()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Act
        var formats = adapter.Syntax.Formats;
        var platforms = adapter.Platform.GetAllPlatforms(adapter.Info.Dialect);

        // Assert — the catalog lists these in the UI, so an empty list is an empty dropdown.
        formats.Should().NotBeEmpty();
        formats.Select(format => format.Id).Should().OnlyHaveUniqueItems();
        formats.Should().OnlyContain(format => format.Provider == "postgresql");
        platforms.Should().NotBeEmpty("the .platform files ship beside the adapter");

        foreach (var platform in platforms)
        {
            var defaults = adapter.Platform.GetConnectionDefaults(adapter.Info.Dialect, platform.Id);

            defaults.Port.Should().BePositive(
                $"'{platform.Id}' is offered in the connection dialog, which pre-fills from its port");
            adapter.Platform.GetAllPlatforms(adapter.Info.Dialect)
                .Should().Contain(platform, "a listed platform stays listed");
        }
    }

    [Fact]
    public void RoundTripFormatsReadBackTheirOwnExport()
    {
        // Arrange — these four write text their own detector accepts. A format that writes something
        // it then rejects is a one-way door: the user cannot paste its output back in.
        string[] roundTripIds = ["pg-uri", "pg-jdbc", "pg-npgsql", "pg-libpq"];
        var formats = CreateAdapter().Syntax.Formats;

        // Act
        var roundTrips = formats
            .Where(format => roundTripIds.Contains(format.Id))
            .Select(format =>
            {
                var text = format.Export(Fields());

                return new { format.Id, Text = text, Recognized = format.CanImport(text) };
            })
            .ToList();

        // Assert
        roundTrips.Should().HaveCount(roundTripIds.Length);
        roundTrips.Should().OnlyContain(
            trip => trip.Text.Length > 0 && trip.Recognized,
            "what a round-trip format exports has to be something it detects");
    }

    [Fact]
    public void ImportOnlyFormatsRefuseExport()
    {
        // Arrange — these read an environment block the user pastes and have no export of their own;
        // the store dialog must not offer them, so CanExport has to stay false.
        string[] importOnlyIds = ["pg-envvar", "pg-supabase-env"];
        var formats = CreateAdapter().Syntax.Formats;

        // Act
        var importOnly = formats.Where(format => importOnlyIds.Contains(format.Id)).ToList();

        // Assert
        importOnly.Should().HaveCount(importOnlyIds.Length);
        foreach (var format in importOnly)
        {
            format.CanExport(Fields()).Should().BeFalse("an import-only format has nothing to write");
            format.DetectionPriority.Should().BeLessThan(
                int.MaxValue,
                "an import-only format is still detected when the user pastes it");
            format.Invoking(f => f.Export(Fields())).Should().Throw<NotSupportedException>();
        }
    }

    [Fact]
    public void SupabaseFormatWritesAPlainUriAndDetectsOnlySupabaseHosts()
    {
        // Arrange — Supabase shares the plain URI syntax; its export is that URI unchanged, so only
        // a .supabase.co host marks the text as Supabase again.
        var format = CreateAdapter().Syntax.Formats.Single(f => f.Id == "pg-supabase");
        var supabaseText =
            "postgresql://postgres.testrefxyz:not-a-real-password@aws-0-test-region.pooler.supabase.co:6543/postgres";

        // Act
        var exported = format.Export(Fields());

        // Assert
        exported.Should().StartWith("postgresql://");
        format.CanImport(exported).Should().BeFalse("the exported host is not a Supabase host");
        format.CanImport(supabaseText).Should().BeTrue();
        format.Parse(supabaseText).Options.Should().ContainKey("SupabaseProjectRef")
            .WhoseValue.Should().Be("testrefxyz");
    }

    [Fact]
    public void ExportOnlyFormatsStayOutOfDetection()
    {
        // Arrange — these carry the ecosystem's own syntax, which is not what the import dialog can
        // accept. Pinning it keeps a later change from claiming a format the parser cannot read.
        string[] exportOnlyIds = ["pg-node", "pg-prisma", "pg-sqlalchemy"];
        var adapter = CreateAdapter();

        // Act
        var exportOnly = adapter.Syntax.Formats
                                .Where(format => exportOnlyIds.Contains(format.Id))
                                .ToList();

        // Assert
        exportOnly.Should().HaveCount(exportOnlyIds.Length);
        foreach (var format in exportOnly)
        {
            var text = format.Export(Fields());
            text.Should().NotBeNullOrWhiteSpace();
            format.CanImport(text).Should().BeFalse("an export-only format is never detected");
            format.DetectionPriority.Should().Be(int.MaxValue);
            format.Invoking(f => f.Parse(text)).Should().Throw<NotSupportedException>();
        }
    }

    private static ConnectionStringFields Fields() =>
        new(
            Host: "test.example.com",
            Port: 5432,
            Database: "testdb",
            Username: "test_user",
            Password: "not-a-real-password",
            SslMode: ESslMode.Prefer,
            Provider: "postgresql");

    private static PostgreSqlAdapter CreateAdapter() =>
        new(NullLoggerFactory.Instance, AppContext.BaseDirectory);

    private static DatabaseTarget Target() =>
        new(
            "postgresql",
            "Host=test.example.com;Port=5432;Database=testdb;Username=test_user;Password=not-a-real-password");
}
