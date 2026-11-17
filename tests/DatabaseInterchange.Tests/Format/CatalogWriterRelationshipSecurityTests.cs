using CanonicalModel.Interchange;

using DatabaseInterchange.Format;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// The two sections whose content is not a list of <see cref="ISchemaObject"/>: relationships, which
/// are pairs of refs rather than objects, and security, which is one compound object holding three
/// lists of them. Both live under <c>schema/</c> like every other section, so both are declared in
/// <c>catalog.json</c> and hashed into <c>manifest.json</c> — a file written around the writer would
/// be readable and unverifiable at the same time.
/// </summary>
public sealed class CatalogWriterRelationshipSecurityTests : IDisposable
{
    private readonly string _catalogPath;

    public CatalogWriterRelationshipSecurityTests() =>
        _catalogPath = Path.Combine(
            Path.GetTempPath(),
            "DbInterchangeRelSecTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task WriteRelationshipsAsync_isListedHashedAndReadBackByTheGenericSectionReader()
    {
        // Arrange
        var (customers, orders, view) = TableRefs();
        var edges = new List<Relationship>
        {
            new() { Type = "viewReference", DependentRef = view, DependencyRef = customers },
            new() { Type = "foreignKey", DependentRef = orders, DependencyRef = customers },
        };

        // Act
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            (await writer.WriteRelationshipsAsync(edges)).Should().Be(2);
            await writer.CompleteAsync();
        }

        // Assert
        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.Files.Should().Contain(f =>
            f.Path == "schema/relationships.json" && f.Bytes > 0);
        reader.Index.Sections.Should().Contain("relationships");

        // The generic list reader takes this file with no new API: relationships keep the header and
        // array shape, only the object type differs.
        var read = await reader.ReadSectionAsync<Relationship>("relationships");
        read.Should().BeEquivalentTo(edges);

        (await new CatalogVerifier().VerifyAsync(_catalogPath)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task WriteRelationshipsAsync_ordersEdgesSoOneDatabaseStateIsOneFile()
    {
        // Arrange — the same three edges in two arrival orders. The metadata reader's output order is
        // not part of the database's identity, so it must not reach the file.
        var (customers, orders, view) = TableRefs();
        var edges = new List<Relationship>
        {
            new() { Type = "viewReference", DependentRef = view, DependencyRef = orders },
            new() { Type = "foreignKey", DependentRef = orders, DependencyRef = customers },
            new() { Type = "viewReference", DependentRef = view, DependencyRef = customers },
        };
        var filePath = Path.Combine(_catalogPath, "schema", "relationships.json");

        byte[] first;
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            await writer.WriteRelationshipsAsync(edges);
            await writer.CompleteAsync();
        }

        first = await File.ReadAllBytesAsync(filePath);

        // Act — one catalog's worth of edges, listed in reverse, written over the same directory.
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql",
                         overwrite: true))
        {
            await writer.WriteRelationshipsAsync([edges[2], edges[1], edges[0]]);
            await writer.CompleteAsync();
        }

        // Assert
        var second = await File.ReadAllBytesAsync(filePath);
        second.Should().Equal(first);

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        var read = await reader.ReadSectionAsync<Relationship>("relationships");
        read.Select(r => r.Type).Should().Equal("foreignKey", "viewReference", "viewReference");

        // Edges of one kind are ordered by the refs they name, so the order is stated rather than
        // guessed from a hash value the test cannot know in advance.
        var sameKind = read.Skip(1).Select(r => r.DependencyRef.ToString()).ToList();
        string.CompareOrdinal(sameKind[0], sameKind[1]).Should().BeNegative();
        sameKind.Should().HaveCount(2);
    }

    [Fact]
    public async Task WriteRelationshipsAsync_withNoEdges_isAnEmptySectionRatherThanAnAbsentOne()
    {
        // Unlike security, this capture is complete: the source really was asked for every dependency
        // and reported none, so an empty section file states a checked fact.
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            (await writer.WriteRelationshipsAsync([])).Should().Be(0);
            await writer.CompleteAsync();
        }

        File.Exists(Path.Combine(_catalogPath, "schema", "relationships.json")).Should().BeTrue();

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Index.Sections.Should().Contain("relationships");
        (await reader.ReadSectionAsync<Relationship>("relationships")).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteSecurityAsync_carriesThePoliciesAndSurvivesTheRoundTrip()
    {
        // Arrange
        var (customers, _, _) = TableRefs();
        var security = new SecuritySection
        {
            Policies =
            [
                new PolicyDefinition
                {
                    Ref = ObjectRef.Policy("public", "customers", "customers_read"),
                    Name = "customers_read",
                    TableRef = customers,
                    Command = "SELECT",
                    IsPermissive = false,
                    ApplicableRoles = ["test_user"],
                    Using = new DatabaseExpression("postgresql", "true"),
                },
            ],
        };

        // Act
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            (await writer.WriteSecurityAsync(security)).Should().BeTrue();
            await writer.CompleteAsync();
        }

        // Assert
        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Manifest.Files.Should().Contain(f => f.Path == "schema/security.json" && f.Bytes > 0);
        reader.Index.Sections.Should().Contain("security");

        var read = await reader.ReadSecurityAsync();
        var policy = read!.Policies.Should().ContainSingle().Which;
        policy.Name.Should().Be("customers_read");
        policy.TableRef.Should().Be(customers);
        policy.Command.Should().Be("SELECT");
        policy.IsPermissive.Should().BeFalse();
        policy.ApplicableRoles.Should().Equal("test_user");
        policy.Using!.Text.Should().Be("true");
        policy.Using.Dialect.Should().Be("postgresql");

        // Not captured is recorded as an empty list, not as an invented object.
        read.Roles.Should().BeEmpty();
        read.Grants.Should().BeEmpty();

        (await new CatalogVerifier().VerifyAsync(_catalogPath)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task WriteSecurityAsync_withNothingCaptured_writesNoFileAndClaimsNothing()
    {
        // A file of three empty lists would tell a restore that the source has no roles and no
        // grants — a fact the capture never looked for. Absence is the honest encoding, and the
        // reader keeps it distinguishable from a captured-but-empty section.
        await using (var writer = await CatalogWriter.CreateInterchangeAsync(
                         _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql"))
        {
            (await writer.WriteSecurityAsync(new SecuritySection())).Should().BeFalse();
            await writer.CompleteAsync();
        }

        File.Exists(Path.Combine(_catalogPath, "schema", "security.json")).Should().BeFalse();

        await using var reader = await CatalogReader.OpenAsync(_catalogPath);
        reader.Index.Sections.Should().NotContain("security");
        reader.Manifest.Files.Should().NotContain(f => f.Path == "schema/security.json");
        (await reader.ReadSecurityAsync()).Should().BeNull();
    }

    private static (ObjectRef Customers, ObjectRef Orders, ObjectRef View) TableRefs()
    {
        var schemaRef = ObjectRef.Schema("public");
        return
        (
            ObjectRef.Table("public", "customers"),
            ObjectRef.Table("public", "orders"),
            ObjectRef.View("public", "v_recent_orders")
        );
    }
}
