using DbAdapter.Definitions;
using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The reader's metadata half, tested with a substituted <see cref="IMetadataSource"/>.
/// <para>
/// Row streaming is deliberately not covered here: it opens a connection and drives a server-side
/// cursor, so the only honest test for it is an integration test against a real PostgreSQL. What
/// this does cover is the part the unit can prove — that the reader answers from the plugin's
/// existing metadata role rather than a private copy of it, and that one export reads that model
/// once so its schema pass, its native pass and its row passes cannot disagree about the database.
/// </para>
/// </summary>
public class PostgreSqlReaderTests
{
    private readonly IDatabaseAdapter _adapter = Substitute.For<IDatabaseAdapter>();

    private readonly IMetadataSource _metadata = Substitute.For<IMetadataSource>();

    public PostgreSqlReaderTests() =>
        _adapter.Info.Returns(new AdapterInfo("org.postgresql.adapter", "postgresql", "*", "1.0.0"));

    [Fact]
    public async Task ReadSchemaAsync_ReturnsTheMappedCatalogModel()
    {
        GivenFullModel();
        await using var reader = CreateReader();

        var schema = await reader.ReadSchemaAsync(CancellationToken.None);

        schema.Database.Name.Should().Be("testdb");
        schema.Tables.Should().HaveCount(3);
        schema.Views.Should().HaveCount(2);
        schema.Routines.Should().HaveCount(3);
        schema.Schemas.Select(s => s.Name).Should().Equal("public", "reporting");
    }

    [Fact]
    public async Task ReadNativeObjectsAsync_ReturnsTheKindsTheNeutralModelCannotHold()
    {
        GivenFullModel();
        await using var reader = CreateReader();

        var natives = await reader.ReadNativeObjectsAsync(CancellationToken.None);

        natives.Should().HaveCount(5);
        natives.Select(n => n.ObjectKind).Should()
            .ContainInOrder("customType", "customType", "domain", "publication", "subscription");
    }

    [Fact]
    public async Task Reader_ReadsTheMetadataModelOnceForBothPasses()
    {
        GivenFullModel();
        await using var reader = CreateReader();

        await reader.ReadSchemaAsync(CancellationToken.None);
        await reader.ReadNativeObjectsAsync(CancellationToken.None);

        await _metadata.Received(1).ReadDatabaseModelAsync(
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reader_KeepsTheAdapterItWasHanded()
    {
        GivenFullModel();
        await using var reader = CreateReader();

        reader.Adapter.Should().Be(_adapter);

        var schema = await reader.ReadSchemaAsync(CancellationToken.None);
        schema.Tables.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadSchemaAsync_AfterDispose_IsRefused()
    {
        GivenFullModel();
        var reader = CreateReader();
        await reader.DisposeAsync();

        var act = () => reader.ReadSchemaAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private void GivenFullModel() =>
        _metadata
            .ReadDatabaseModelAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(InterchangeTestModel.Full());

    private PostgreSqlReader CreateReader() =>
        new(
            _adapter,
            TestTargets.Local(),
            new ExportOptions(),
            NullLogger<PostgreSqlReader>.Instance,
            _metadata);
}
