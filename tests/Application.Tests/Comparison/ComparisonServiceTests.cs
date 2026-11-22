using Application.Tests.Relationship;

using CanonicalModel.DTOs;
using CanonicalModel.Enums;
using CanonicalModel.Models;

using DbAdapter.Definitions;
using DbClone.Application.Adapters;
using DbClone.Application.Comparison;
using DbClone.Application.DTOs;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace Application.Tests.Comparison;

/// <summary>
/// The comparison service is what the Compare workspace shows and what the reports are built
/// from, so its own decisions are pinned here: which side is refused, how a table's status is
/// decided, and that one failing comparer cannot take the run down.
/// </summary>
public sealed class ComparisonServiceTests
{
    [Fact]
    public async Task DifferentDialects_AreRefusedBeforeAnyModelIsRead()
    {
        // Arrange — comparing across engines has no meaning, and reading both models first would
        // only make the user wait for an answer that is already known.
        var postgres = Adapter("postgresql");
        var mySql = Adapter("mysql");
        var service = CreateService(Catalog(("postgresql", postgres), ("mysql", mySql)));

        // Act
        var result = await Compare(service, "postgresql", "mysql");

        // Assert
        result.TotalErrors.Should().Be(1);
        result.Items.Should().BeEmpty();
        postgres.DidNotReceive().CreateMetadataSource(Arg.Any<DatabaseTarget>());
        mySql.DidNotReceive().CreateMetadataSource(Arg.Any<DatabaseTarget>());
    }

    [Fact]
    public async Task TableOnlyInSource_IsReportedAsMissingInDestinationWithItsRowCount()
    {
        // Arrange
        var adapter = Adapter("postgresql");
        var comparer = Substitute.For<ITableComparer>();
        adapter.CreateComparer().Returns(comparer);
        MetadataFor(adapter, Model(Table("public", "orders")), Model());

        comparer.CountRowsAsync(Arg.Any<DatabaseTarget>(), "public", "orders", Arg.Any<CancellationToken>())
            .Returns(5L);

        var service = CreateService(Catalog(("postgresql", adapter)));

        // Act
        var result = await Compare(service, "postgresql", "postgresql");

        // Assert
        result.TotalMissingDest.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0].Status.Should().Be(ECompareStatus.MissingDest);
        result.Items[0].TableName.Should().Be("public.orders");
        result.Items[0].SourceCount.Should().Be(5);
        result.Items[0].Details.Should().Contain("exists only in source");
    }

    [Fact]
    public async Task CommonTable_WithEqualRowCounts_IsReportedIdentical()
    {
        // Arrange
        var adapter = Adapter("postgresql");
        var comparer = Substitute.For<ITableComparer>();
        adapter.CreateComparer().Returns(comparer);
        MetadataFor(adapter, Model(Table("public", "orders")), Model(Table("public", "orders")));

        comparer.CompareTableAsync(
                Arg.Any<DatabaseTarget>(),
                Arg.Any<DatabaseTarget>(),
                "public",
                "orders",
                EVerifyMode.RowCount,
                Arg.Any<CancellationToken>())
            .Returns(new TableCompareResult(IsMatch: true, SourceCount: 7, DestCount: 7,
                                            RowsAdded: 0, RowsRemoved: 0, RowsModified: 0));

        var service = CreateService(Catalog(("postgresql", adapter)));

        // Act
        var result = await Compare(service, "postgresql", "postgresql");

        // Assert
        result.TotalIdentical.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Details.Should().BeEmpty();
    }

    [Fact]
    public async Task CommonTable_WithDifferentRowCounts_CarriesTheCountsInItsDetails()
    {
        // Arrange — the detail line is what the user acts on, so it has to name both sides.
        var adapter = Adapter("postgresql");
        var comparer = Substitute.For<ITableComparer>();
        adapter.CreateComparer().Returns(comparer);
        MetadataFor(adapter, Model(Table("public", "orders")), Model(Table("public", "orders")));

        comparer.CompareTableAsync(
                Arg.Any<DatabaseTarget>(),
                Arg.Any<DatabaseTarget>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<EVerifyMode>(),
                Arg.Any<CancellationToken>())
            .Returns(new TableCompareResult(IsMatch: false, SourceCount: 9, DestCount: 4,
                                            RowsAdded: 0, RowsRemoved: 5, RowsModified: 0));

        var service = CreateService(Catalog(("postgresql", adapter)));

        // Act
        var result = await Compare(service, "postgresql", "postgresql");

        // Assert
        result.TotalDifferent.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Details
            .Should().Be("Row count differs (source: 9, dest: 4)");
    }

    [Fact]
    public async Task ModelComparerThatThrows_IsIsolatedAndTheRemainingComparersStillRun()
    {
        // Arrange — one broken comparer must not hide the other categories' findings.
        var adapter = Adapter("postgresql");
        MetadataFor(adapter, Model(), Model());

        var broken = Substitute.For<IModelComparer>();
        broken.Compare(Arg.Any<DatabaseModel>(), Arg.Any<DatabaseModel>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ModelCompareItem>>(_ => throw new InvalidOperationException("comparer failed"));

        var working = Substitute.For<IModelComparer>();
        working.Compare(Arg.Any<DatabaseModel>(), Arg.Any<DatabaseModel>(), Arg.Any<CancellationToken>())
            .Returns([new ModelCompareItem(EDatabaseObjectType.View, "public", "v_orders",
                                           ECompareStatus.Different, "definition differs")]);

        var service = CreateService(Catalog(("postgresql", adapter)), broken, working);

        // Act
        var result = await Compare(service, "postgresql", "postgresql");

        // Assert
        result.TotalErrors.Should().Be(0);
        result.Items.Should().ContainSingle()
            .Which.Should().Match<CompareResultItem>(item =>
                item.ObjectType == EDatabaseObjectType.View
                && item.TableName == "v_orders"
                && item.Status == ECompareStatus.Different);
    }

    [Fact]
    public async Task ActiveTableSelection_ScopesBothSidesSoUnselectedTablesAreNotReported()
    {
        // Arrange — the destination holds a table outside the selection; without scoping it would
        // be reported as missing from the source and read as a difference the user never asked for.
        var adapter = Adapter("postgresql");
        var comparer = Substitute.For<ITableComparer>();
        adapter.CreateComparer().Returns(comparer);
        MetadataFor(
            adapter,
            Model(Table("public", "orders"), Table("public", "customers")),
            Model(Table("public", "orders"), Table("public", "customers"), Table("public", "legacy")));

        comparer.CompareTableAsync(
                Arg.Any<DatabaseTarget>(),
                Arg.Any<DatabaseTarget>(),
                "public",
                "orders",
                Arg.Any<EVerifyMode>(),
                Arg.Any<CancellationToken>())
            .Returns(new TableCompareResult(IsMatch: true, SourceCount: 3, DestCount: 3,
                                            RowsAdded: 0, RowsRemoved: 0, RowsModified: 0));

        var service = CreateService(Catalog(("postgresql", adapter)));
        var selection = TableSelectionSpec.Excluding([new TableId("public", "customers")]);

        // Act
        var result = await Compare(service, "postgresql", "postgresql", selection);

        // Assert
        result.Items.Should().ContainSingle().Which.TableName.Should().Be("public.orders");
        result.TotalMissingSource.Should().Be(0);
    }

    [Fact]
    public async Task CancelledToken_StopsBeforeAnyTableIsCompared()
    {
        // Arrange
        var adapter = Adapter("postgresql");
        var comparer = Substitute.For<ITableComparer>();
        adapter.CreateComparer().Returns(comparer);
        MetadataFor(adapter, Model(Table("public", "orders")), Model(Table("public", "orders")));

        var service = CreateService(Catalog(("postgresql", adapter)));

        // Act
        var act = () => Compare(service, "postgresql", "postgresql",
                                ct: new CancellationToken(canceled: true));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        await comparer.DidNotReceive().CompareTableAsync(
            Arg.Any<DatabaseTarget>(),
            Arg.Any<DatabaseTarget>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<EVerifyMode>(),
            Arg.Any<CancellationToken>());
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static Task<CompareDatabasesResult> Compare(
        IComparisonService service,
        string sourceEngine,
        string destinationEngine,
        TableSelectionSpec? selection = null,
        CancellationToken ct = default) =>
        service.CompareDatabasesAsync(
            Connection("source_db", sourceEngine),
            Connection("dest_db", destinationEngine),
            (_, _) => { },
            EVerifyMode.RowCount,
            excludePlatformSchemas: false,
            progress: null,
            waitWhilePaused: null,
            ct,
            selection);

    private static ComparisonService CreateService(IAdapterCatalog catalog, params IModelComparer[] comparers) =>
        new(
            new DatabaseTargetFactory(catalog),
            comparers,
            new TableFilterApplier(),
            NullLogger<ComparisonService>.Instance);

    private static IAdapterCatalog Catalog(params (string Engine, IDatabaseAdapter Adapter)[] adapters)
    {
        var catalog = Substitute.For<IAdapterCatalog>();
        var map = adapters.ToDictionary(
            entry => entry.Engine,
            entry => entry.Adapter,
            StringComparer.OrdinalIgnoreCase);

        catalog.All.Returns(adapters.Select(entry => entry.Adapter).ToList());
        catalog.Find(Arg.Any<string>()).Returns(call =>
        {
            var engine = call.Arg<string>();

            return engine is not null && map.TryGetValue(engine, out var adapter) ? adapter : null;
        });

        return catalog;
    }

    /// <summary>An adapter whose every role is a substitute the test then configures.</summary>
    private static IDatabaseAdapter Adapter(string dialect)
    {
        var syntax = Substitute.For<IConnectionStringSyntax>();
        syntax.BuildKeyValue(Arg.Any<ConnectionStringFields>())
            .Returns(call => "Host=test.example.com;Database=" + call.Arg<ConnectionStringFields>()?.Database);

        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(new AdapterInfo($"test.{dialect}", dialect, "*", "1.0.0"));
        adapter.Syntax.Returns(syntax);
        adapter.CreateModelComparers().Returns([]);

        return adapter;
    }

    /// <summary>
    /// Hands the two models to the adapter, keyed by the database each target names — the service
    /// reads the source model first and the destination model second.
    /// </summary>
    private static void MetadataFor(
        IDatabaseAdapter adapter,
        DatabaseModel source,
        DatabaseModel destination)
    {
        var sourceMetadata = Metadata(source);
        var destinationMetadata = Metadata(destination);

        adapter.CreateMetadataSource(Arg.Is<DatabaseTarget>(t => t != null && t.ConnectionString.Contains("source_db")))
            .Returns(sourceMetadata);
        adapter.CreateMetadataSource(Arg.Is<DatabaseTarget>(t => t != null && t.ConnectionString.Contains("dest_db")))
            .Returns(destinationMetadata);
    }

    private static IMetadataSource Metadata(DatabaseModel model)
    {
        var metadata = Substitute.For<IMetadataSource>();
        metadata.ReadDatabaseModelAsync(
                Arg.Any<bool>(),
                EMetadataScope.Everything,
                Arg.Any<CancellationToken>())
            .Returns(model);

        return metadata;
    }

    private static DatabaseModel Model(params TableDefinition[] tables) =>
        RelationshipModelFixture.Model(tables);

    private static TableDefinition Table(string schema, string name) =>
        RelationshipModelFixture.Table(schema, name);

    private static ConnectionInfo Connection(string database, string engine) =>
        new("test.example.com", 5432, database, "test_user", "not-a-real-password", "Prefer", engine);
}
