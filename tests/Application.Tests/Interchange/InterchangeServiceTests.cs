using DbClone.Application.DTOs;
using DbClone.Application.Interchange;
using DbClone.Application.TableFilter;
using DbClone.Application.Targets;

using FluentAssertions;

using NSubstitute;

using Microsoft.Extensions.Logging;

namespace Application.Tests.Interchange;

/// <summary>
/// Unit tests for <see cref="InterchangeService"/> — validates request handling
/// and the stub behaviour while the interchange product is not yet available.
/// </summary>
public class InterchangeServiceTests
{
    private readonly DatabaseTargetFactory _targetFactory;
    private readonly ILogger<InterchangeService> _logger;
    private readonly InterchangeService _sut;

    public InterchangeServiceTests()
    {
        var adapter = Substitute.For<DbAdapter.Definitions.IDatabaseAdapter>();
        adapter.Info.Returns(new DbAdapter.Definitions.AdapterInfo("test.postgresql", "postgresql", "*", "1.0.0"));
        adapter.Syntax.Returns(Substitute.For<DbAdapter.Definitions.IConnectionStringSyntax>());
        adapter.Syntax.BuildKeyValue(Arg.Any<CanonicalModel.DTOs.ConnectionStringFields>())
            .Returns("Host=test.example.com;Port=5432;Database=testdb;Username=test_user");

        _targetFactory = new DatabaseTargetFactory(adapter);
        _logger = Substitute.For<ILogger<InterchangeService>>();
        _sut = new InterchangeService(_targetFactory, _logger);
    }

    [Fact]
    public void StoreAsync_throws_NotSupportedException_until_product_arrives()
    {
        // Arrange
        var request = new InterchangeStoreRequest(
            Connection: new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "pass", CanonicalModel.Enums.ESslMode.Prefer),
            TableSelection: TableSelectionSpec.All,
            CatalogPath: @"C:\test\catalog.rdbx",
            ChunkBytes: 8_388_608,
            RowsPerChunk: 50_000,
            IncludeData: true,
            SnapshotConsistency: "consistent",
            UnsupportedFeaturePolicy: "fail",
            PackAfterStore: true,
            VerifyAfterStore: true);

        // Act
        var act = () => _sut.StoreAsync(request, null, CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*DatabaseInterchange*not yet available*");
    }

    [Fact]
    public void PlanRestoreAsync_throws_NotSupportedException_until_product_arrives()
    {
        // Act
        var act = () => _sut.PlanRestoreAsync(@"C:\test\catalog.rdbx", CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*DatabaseInterchange*not yet available*");
    }

    [Fact]
    public void RestoreAsync_throws_NotSupportedException_until_product_arrives()
    {
        // Arrange
        var request = new InterchangeRestoreRequest(
            CatalogPath: @"C:\test\catalog.rdbx",
            Connection: new ConnectionInfo("test.example.com", 5432, "testdb", "test_user", "pass", CanonicalModel.Enums.ESslMode.Prefer),
            UnsupportedFeaturePolicy: "fail");

        // Act
        var act = () => _sut.RestoreAsync(request, null, CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*DatabaseInterchange*not yet available*");
    }

    [Fact]
    public void InspectAsync_throws_NotSupportedException_until_product_arrives()
    {
        // Act
        var act = () => _sut.InspectAsync(@"C:\test\catalog.rdbx", CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*DatabaseInterchange*not yet available*");
    }

    [Fact]
    public void VerifyAsync_throws_NotSupportedException_until_product_arrives()
    {
        // Act
        var act = () => _sut.VerifyAsync(@"C:\test\catalog.rdbx", CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*DatabaseInterchange*not yet available*");
    }

    [Fact]
    public void PackAsync_throws_NotSupportedException_until_product_arrives()
    {
        // Act
        var act = () => _sut.PackAsync(@"C:\test\catalog.rdbx", CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*DatabaseInterchange*not yet available*");
    }

    [Fact]
    public void StoreAsync_validates_request_not_null()
    {
        // Act
        var act = () => _sut.StoreAsync(null!, null, CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void PlanRestoreAsync_validates_catalog_path_not_empty()
    {
        // Act
        var act = () => _sut.PlanRestoreAsync("", CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void RestoreAsync_validates_request_not_null()
    {
        // Act
        var act = () => _sut.RestoreAsync(null!, null, CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<ArgumentNullException>();
    }
}
