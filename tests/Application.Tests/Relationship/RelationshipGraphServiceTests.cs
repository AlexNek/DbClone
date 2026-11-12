using CanonicalModel.Models;

using DbAdapter.Definitions;

using DbClone.Application.Adapters;
using DbClone.Application.DTOs;
using DbClone.Application.Interchange;
using DbClone.Application.Relationship;
using DbClone.Application.Targets;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace Application.Tests.Relationship;

/// <summary>
/// The two reads the service offers: a catalog delegates to the product's single deserialization path,
/// and a live read goes through the adapter's metadata role with the same platform-schema rule the
/// store's own reader uses, stating an origin that carries no user and no password.
/// </summary>
public class RelationshipGraphServiceTests
{
    private static readonly ConnectionInfo Connection = new(
        Host: "test.example.com",
        Port: 5432,
        DatabaseName: "testdb",
        Username: "test_user",
        Password: "fake#password",
        SslMode: "Prefer");

    [Fact]
    public async Task ReadCatalogAsync_DelegatesToTheInterchangeRead()
    {
        // Arrange
        var interchange = Substitute.For<IInterchangeService>();
        var view = ViewOf(EGraphOriginKind.Catalog);
        interchange.ReadGraphAsync(@"C:\catalogs\testdb", Arg.Any<CancellationToken>()).Returns(view);
        var sut = CreateService(interchange, out _, out _);

        // Act
        var graph = await sut.ReadCatalogAsync(@"C:\catalogs\testdb", CancellationToken.None);

        // Assert
        graph.Should().BeSameAs(view);
        await interchange.Received(1).ReadGraphAsync(@"C:\catalogs\testdb", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadCatalogAsync_RejectsABlankPath()
    {
        // Arrange
        var sut = CreateService(Substitute.For<IInterchangeService>(), out _, out _);

        // Act
        var act = () => sut.ReadCatalogAsync("  ", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadDatabaseAsync_ReadsTheModelWithPlatformSchemasIncluded()
    {
        // Arrange
        var model = RelationshipModelFixture.Model(
            RelationshipModelFixture.Table("public", "customers", columns: [RelationshipModelFixture.Column("id", 1)]));
        var sut = CreateService(Substitute.For<IInterchangeService>(), out var metadata, out _);
        metadata.ReadDatabaseModelAsync(false, Arg.Any<CancellationToken>()).Returns(model);

        // Act
        var graph = await sut.ReadDatabaseAsync(Connection, ERelationshipRole.Source, CancellationToken.None);

        // Assert — `false` is the flag the store's own reader uses (D2); `true` would silently drop
        // platform-managed schemas and every schema the connected role does not own.
        await metadata.Received(1).ReadDatabaseModelAsync(
            excludePlatformSchemas: false,
            ct: Arg.Any<CancellationToken>());
        graph.Tables.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadDatabaseAsync_StatesTheOriginFromTheConnection()
    {
        // Arrange
        var sut = CreateService(Substitute.For<IInterchangeService>(), out var metadata, out _);
        metadata.ReadDatabaseModelAsync(false, Arg.Any<CancellationToken>())
            .Returns(RelationshipModelFixture.Model());

        // Act
        var graph = await sut.ReadDatabaseAsync(Connection, ERelationshipRole.Target, CancellationToken.None);

        // Assert
        graph.Origin.Kind.Should().Be(EGraphOriginKind.LiveDatabase);
        graph.Origin.Label.Should().Be("testdb");
        graph.Origin.Location.Should().Be("test.example.com:5432/testdb");
        graph.Origin.Dialect.Should().Be("postgresql");
        graph.Origin.FormatVersion.Should().BeNull("a live read declares no interchange format version");
        graph.HasData.Should().BeFalse("a live read counts no rows (D9)");
    }

    [Fact]
    public async Task ReadDatabaseAsync_KeepsTheUserAndPasswordOutOfTheOrigin()
    {
        // Arrange
        var sut = CreateService(Substitute.For<IInterchangeService>(), out var metadata, out _);
        metadata.ReadDatabaseModelAsync(false, Arg.Any<CancellationToken>())
            .Returns(RelationshipModelFixture.Model());

        // Act
        var graph = await sut.ReadDatabaseAsync(Connection, ERelationshipRole.Source, CancellationToken.None);

        // Assert — neither the user nor the password can reach a document, a title or a log line.
        var stated = string.Join(
            '\n',
            graph.Origin.Label,
            graph.Origin.Location,
            graph.Origin.Dialect,
            graph.Origin.Kind.ToString());
        stated.Should().NotContain(Connection.Username);
        stated.Should().NotContain(Connection.Password);
    }

    [Fact]
    public async Task ReadDatabaseAsync_ReportsAnUnknownEngineInsteadOfFallingBack()
    {
        // Arrange
        var sut = CreateService(Substitute.For<IInterchangeService>(), out _, out _);

        // Act — this fixture's catalog holds no adapter that speaks `unknown`.
        var act = () => sut.ReadDatabaseAsync(
            Connection with { Engine = "unknown" },
            ERelationshipRole.Source,
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<DbClone.Application.Exceptions.EngineSelectionException>();
    }

    private static RelationshipGraphService CreateService(
        IInterchangeService interchange,
        out IMetadataSource metadata,
        out IDatabaseAdapter adapter)
    {
        adapter = Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(new AdapterInfo("test.adapter", "postgresql", "*", "1.0.0"));
        adapter.Syntax.Returns(Substitute.For<IConnectionStringSyntax>());

        metadata = Substitute.For<IMetadataSource>();
        adapter.CreateMetadataSource(Arg.Any<DatabaseTarget>()).Returns(metadata);

        var catalog = Substitute.For<IAdapterCatalog>();
        catalog.All.Returns([adapter]);
        // A dialect this fixture does not speak resolves to nothing, as an installed set without that
        // engine would: the selection rule must report it rather than fall back to the only adapter.
        catalog.Find(Arg.Any<string>()).Returns((IDatabaseAdapter?)null);
        catalog.Find("postgresql").Returns(adapter);

        return new RelationshipGraphService(
            interchange,
            new DatabaseTargetFactory(catalog),
            NullLogger<RelationshipGraphService>.Instance);
    }

    private static RelationshipGraphView ViewOf(EGraphOriginKind kind) =>
        new(
            Origin: new GraphViewOrigin(kind, "testdb", "test.example.com:5432/testdb", "postgresql", null),
            HasData: false,
            Tables: [],
            OutsideTables: [],
            Schemas: [],
            Edges: [],
            UnrelatedTableCount: 0);
}
