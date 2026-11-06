using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Pipeline;
using DbClone.Application.Copy;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The metadata row describes the scope the application asked for: the stage reports the
/// counts of the model it was handed and reads no catalog of its own.
/// </summary>
public class ReadMetadataStageTests
{
    private readonly ReadMetadataStage _stage = new(NullLoggerFactory.Instance);

    [Fact]
    public async Task ExecuteAsync_FilteredModel_ReportsTheFilteredCounts()
    {
        // Arrange — the two surviving tables and the one view of a larger database
        var model = Model(
            tables: [Table("public", "orders"), Table("public", "items")],
            views: [new ViewDefinition("public", "orders_view", "SELECT ...", null)]);
        var context = Context(model);

        // Act
        var result = await _stage.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.ObjectsProcessed.Should().Be(3);
        CountOf(result, EDatabaseObjectType.Table).Should().Be(2);
        CountOf(result, EDatabaseObjectType.View).Should().Be(1);
        CountOf(result, EDatabaseObjectType.Sequence).Should().Be(0);
        CountOf(result, EDatabaseObjectType.Function).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoModel_FailsInsteadOfReportingAnEmptyDatabase()
    {
        // Arrange
        var context = new CopyContext
        {
            Request = new CopyRequest(
                TestTargets.Local(), TestTargets.Local(), Model(), new CopyOptions())
        };

        // Act
        var act = () => _stage.ExecuteAsync(context);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static CopyContext Context(DatabaseModel model) => new()
    {
        Request = new CopyRequest(TestTargets.Local(), TestTargets.Local(), model, new CopyOptions()),
        SourceModel = model
    };

    private static int CountOf(StageResult result, EDatabaseObjectType objectType) =>
        result.Details
            .Single(detail => detail.Kind == EStageMessageKind.Count
                              && detail.Get<EDatabaseObjectType>(PropKeys.ObjectType) == objectType)
            .Get<int>(PropKeys.Count);

    private static DatabaseModel Model(
        IReadOnlyList<TableDefinition>? tables = null,
        IReadOnlyList<ViewDefinition>? views = null) =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "17.0",
            Schemas: [],
            Tables: tables ?? [],
            Views: views ?? [],
            MaterializedViews: [],
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

    private static TableDefinition Table(string schema, string name) =>
        new(schema, name, [], [], [], [], [], null, false, null, null);
}
