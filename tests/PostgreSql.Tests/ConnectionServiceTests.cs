using DbClone.Application.Enums;
using DbClone.Application.Exceptions;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.Application.Services;
using DbClone.PostgreSql.Formats;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

namespace PostgreSql.Tests;

/// <summary>
/// Tests for the provider-agnostic import/export orchestration services.
/// Uses the real format implementations to validate detection ordering
/// (most-specific-first) and first-match-wins behaviour end to end.
/// </summary>
public class ConnectionImportServiceTests
{
    [Fact]
    public void Detect_UnknownText_ReturnsNone() =>
        CreateService().Detect("not a connection string").IsDetected.Should().BeFalse();

    [Fact]
    public void Detect_UriString_ReturnsDetected()
    {
        var detection = CreateService().Detect("postgresql://user:pass@localhost/db");

        detection.IsDetected.Should().BeTrue();
        detection.FormatId.Should().Be("pg-uri");
        detection.Provider.Should().Be(EDatabaseProvider.PostgreSql);
        detection.Confidence.Should().Be(1.0);
    }

    [Fact]
    public void GetAllFormats_ReturnsOrderedByPriority() =>
        CreateService().GetAllFormats().Should().BeInAscendingOrder(f => f.DetectionPriority);

    [Fact]
    public void Import_DatabaseUrlEnvVar_DetectsEnvVarFormat()
    {
        var result = CreateService()
            .Import("DATABASE_URL=postgresql://admin:s3cret@db.example.com:5433/sales");

        result.Success.Should().BeTrue();
        result.DetectedFormatName.Should().Be("Environment Variable");
        result.Connection!.Host.Should().Be("db.example.com");
    }

    [Fact]
    public void Import_EmptyText_ReturnsFailed()
    {
        var result = CreateService().Import("   ");

        result.Success.Should().BeFalse();
        result.Warnings.Should().ContainSingle(w => w.Level == EWarningLevel.Error);
    }

    [Fact]
    public void Import_JdbcString_DetectsJdbcFormat()
    {
        var result = CreateService()
            .Import("jdbc:postgresql://localhost:5432/db?user=admin&password=pass");

        result.Success.Should().BeTrue();
        result.DetectedFormatName.Should().Be("JDBC");
        result.Connection!.Username.Should().Be("admin");
    }

    [Fact]
    public void Import_LibpqString_DetectsLibpqFormat()
    {
        var result = CreateService().Import(
            "host=localhost port=5432 dbname=mydb user=postgres password=secret");

        result.Success.Should().BeTrue();
        result.DetectedFormatName.Should().Be("libpq / psql");
        result.Connection!.Database.Should().Be("mydb");
    }

    [Fact]
    public void Import_MissingPassword_AddsWarning()
    {
        var result = CreateService().Import("postgresql://user@localhost:5432/mydb");

        result.Success.Should().BeTrue();
        result.Warnings.Should().Contain(w =>
            w.Level == EWarningLevel.Warning && w.ParameterName == "Password");
    }

    [Fact]
    public void Import_NpgsqlString_DetectsNpgsqlFormat()
    {
        var result = CreateService().Import(
            "Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass");

        result.Success.Should().BeTrue();
        result.DetectedFormatName.Should().Be("Npgsql / .NET");
        result.Connection!.Database.Should().Be("mydb");
    }

    [Fact]
    public void Import_SslDisabled_AddsInfoWarning()
    {
        var result = CreateService()
            .Import("postgresql://user:pass@localhost:5432/mydb?sslmode=disable");

        result.Warnings.Should()
            .Contain(w => w.Level == EWarningLevel.Info && w.ParameterName == "SslMode");
    }

    [Fact]
    public void Import_SupabaseUri_DetectsSupabaseFormat()
    {
        var result = CreateService()
            .Import(
                "postgresql://postgres.abc:secret@aws-0-us-east-1.pooler.supabase.co:6543/postgres");

        result.Success.Should().BeTrue();
        result.DetectedFormatName.Should().Be("Supabase URI");
        result.Connection!.Options.Should().ContainKey("SupabaseProjectRef").WhoseValue.Should()
            .Be("abc");
    }

    [Fact]
    public void Import_UnknownParams_PreservedInOptions()
    {
        var result = CreateService()
            .Import("postgresql://user:pass@localhost:5432/mydb?ApplicationName=x");

        result.Success.Should().BeTrue();
        result.Connection!.Options.Should().ContainKey("ApplicationName").WhoseValue.Should()
            .Be("x");
    }

    [Fact]
    public void Import_UnrecognizedText_ThrowsUnsupported()
    {
        var act = () => CreateService().Import("this is not a connection string");
        act.Should().Throw<UnsupportedConnectionFormatException>();
    }

    [Fact]
    public void Import_UriString_DetectsUriFormat()
    {
        var result = CreateService().Import("postgresql://admin:s3cret@db.example.com:5433/sales");

        result.Success.Should().BeTrue();
        result.DetectedFormatName.Should().Be("PostgreSQL URI");
        result.Connection!.Host.Should().Be("db.example.com");
    }

    private static ConnectionImportService CreateService()
    {
        // Deliberately registered out of priority order — the service must sort them.
        IConnectionFormat[] formats =
            [
                new PostgreSqlNodeFormat(),
                new PostgreSqlLibpqFormat(),
                new PostgreSqlUriFormat(),
                new PostgreSqlEnvVarFormat(),
                new PostgreSqlSupabaseFormat(),
                new PostgreSqlPrismaFormat(),
                new PostgreSqlJdbcFormat(),
                new PostgreSqlSqlAlchemyFormat(),
                new PostgreSqlNpgsqlFormat()
            ];
        return new ConnectionImportService(formats, NullLogger<ConnectionImportService>.Instance);
    }
}

public class ConnectionExportServiceTests
{
    [Fact]
    public void Export_FormatIdCaseInsensitive_Works() =>
        CreateService().Export(Sample(), "PG-URI").Should().StartWith("postgresql://");

    [Fact]
    public void Export_ImportOnlyFormat_ThrowsInvalidOperation()
    {
        var act = () => CreateService().Export(Sample(), "pg-envvar");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Export_UnknownFormatId_ThrowsArgument()
    {
        var act = () => CreateService().Export(Sample(), "does-not-exist");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Export_UriFormatId_ProducesUri() =>
        CreateService().Export(Sample(), "pg-uri")
            .Should().Be("postgresql://user:pass@localhost:5432/mydb");

    [Fact]
    public void GetSupportedFormats_PostgreSqlConnection_ExcludesImportOnly()
    {
        var ids = CreateService().GetSupportedFormats(Sample()).Select(f => f.Id).ToList();

        ids.Should().NotContain("pg-envvar");
        ids.Should().Contain("pg-uri");
    }

    [Fact]
    public void GetSupportedFormats_ReturnsOnlyExportableFormats()
    {
        var sample = Sample();
        var formats = CreateService().GetSupportedFormats(sample);

        formats.Should().OnlyContain(f => f.CanExport(sample));
    }

    private static ConnectionExportService CreateService()
    {
        IConnectionFormat[] formats =
            [
                new PostgreSqlUriFormat(),
                new PostgreSqlJdbcFormat(),
                new PostgreSqlNpgsqlFormat(),
                new PostgreSqlLibpqFormat(),
                new PostgreSqlEnvVarFormat(),
                new PostgreSqlSqlAlchemyFormat(),
                new PostgreSqlPrismaFormat(),
                new PostgreSqlNodeFormat()
            ];
        return new ConnectionExportService(formats, NullLogger<ConnectionExportService>.Instance);
    }

    private static DatabaseConnection Sample() =>
        new()
            {
                Provider = EDatabaseProvider.PostgreSql,
                Host = "localhost",
                Port = 5432,
                Database = "mydb",
                Username = "user",
                Password = "pass",
                SslMode = ESslMode.Prefer
            };
}
