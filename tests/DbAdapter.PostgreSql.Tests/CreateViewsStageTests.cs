using DbAdapter.Definitions;
using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Interfaces;
using DbAdapter.PostgreSql.Ddl;
using DbAdapter.PostgreSql.Execution;
using DbAdapter.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CanonicalModel.Models;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Hermetic tests for <see cref="CreateViewsStage"/>: the executor is substituted and the
/// destination connection is constructed but never opened, so the suite runs without a live
/// database. A refused view or materialized view is one object lost — one warning naming it
/// and a stage that still succeeds — never a failed run.
/// </summary>
public class CreateViewsStageTests
{
    private readonly ISqlExecutor _executor = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly IPgConnectionProvider _connectionProvider = Substitute.For<IPgConnectionProvider>();
    private readonly CreateViewsStage _stage;

    public CreateViewsStageTests()
    {
        _factory.Create(Arg.Any<NpgsqlConnection>()).Returns(_executor);
        _connectionProvider.GetDestinationConnectionAsync(
                Arg.Any<CopyContext>(),
                Arg.Any<Microsoft.Extensions.Logging.ILogger>(),
                Arg.Any<CancellationToken>())
            .Returns(DestConnection);
        _executor.ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _stage = new CreateViewsStage(
            new PgDdlGenerator(),
            _factory,
            _connectionProvider,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_EveryObjectCreated_ReportsCreatedOverTotal()
    {
        // Arrange
        var context = CreateContext(
            [MakeView("public", "orders_view"), MakeView("public", "customer_view")],
            [MakeMaterializedView("public", "sales_totals")]);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        context.Warnings.Should().BeEmpty();
        context.Errors.Should().BeEmpty();
        context.Statistics.ViewsCopied.Should().Be(3);

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(3);
        summary.Get<int>(PropKeys.Matched).Should().Be(3);
        summary.Get<int>(PropKeys.Failed).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ViewRefused_ReportsOneWarningAndSucceeds()
    {
        // Arrange
        _executor
            .ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql.Contains("broken_view", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("missing relation"));
        var context = CreateContext(
            [MakeView("public", "good_view"), MakeView("public", "broken_view")],
            []);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("a refused view is one object lost, not a failed run");
        context.Errors.Should().BeEmpty();
        context.Warnings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                StageName = ECopyStage.CreateViews,
                Kind = EStageMessageKind.Failed,
                ObjectName = "public.broken_view"
            },
            opts => opts.ExcludingMissingMembers());
        context.Statistics.ViewsCopied.Should().Be(1);
        result.Details.Should()
                .ContainSingle(
                    d => d.Kind == EStageMessageKind.Failed
                        && d.Level == ELogLevel.Warning
                        && d.ObjectName == "public.broken_view");
        result.Details.Should().NotContain(d => d.Level == ELogLevel.Error);

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(2);
        summary.Get<int>(PropKeys.Matched).Should().Be(1);
        summary.Get<int>(PropKeys.Failed).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_MaterializedViewRefused_ReportsOneWarningAndSucceeds()
    {
        // Arrange
        _executor
            .ExecuteNonQueryAsync(
                Arg.Is<string>(sql => sql.Contains("broken_mv", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("missing relation"));
        var context = CreateContext([], [MakeMaterializedView("public", "broken_mv")]);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        context.Errors.Should().BeEmpty();
        context.Warnings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                StageName = ECopyStage.CreateViews,
                Kind = EStageMessageKind.Failed,
                ObjectName = "public.broken_mv"
            },
            opts => opts.ExcludingMissingMembers());
        context.Statistics.ViewsCopied.Should().Be(0);

        var summary = result.Details.Single(d => d.Kind == EStageMessageKind.Summary);
        summary.Get<int>(PropKeys.Total).Should().Be(1);
        summary.Get<int>(PropKeys.Failed).Should().Be(1);
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static NpgsqlConnection DestConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    private static CopyContext CreateContext(
        IReadOnlyList<ViewDefinition> views,
        IReadOnlyList<MaterializedViewDefinition> materializedViews)
    {
        var model = new DatabaseModel(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: [],
            Views: views,
            MaterializedViews: materializedViews,
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: [],
            Policies: [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);
        var target = TestTargets.Local();
        var request = new CopyRequest(target, target, model, new CopyOptions());
        return new CopyContext
        {
            Request = request,
            SourceModel = model,
            DestinationConnection = new PgCopyConnection(DestConnection, string.Empty)
        };
    }

    private static ViewDefinition MakeView(string schema, string name) =>
        new(schema, name, "SELECT 1 AS id", null);

    private static MaterializedViewDefinition MakeMaterializedView(string schema, string name) =>
        new(schema, name, "SELECT 1 AS id", null, [], null);
}
