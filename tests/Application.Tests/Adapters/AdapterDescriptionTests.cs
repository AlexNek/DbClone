using CanonicalModel.Platforms;

using DbAdapter.Definitions;

using DbClone.Application.Adapters;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace Application.Tests.Adapters;

/// <summary>
/// The catalog reads a plugin's contract values through a guard. A plugin that loads and
/// constructs but throws while answering one of them must cost that one folder, never the
/// catalog — the outcome the "a bad plugin is skipped, never fatal" contract promises.
/// </summary>
public sealed class AdapterDescriptionTests
{
    [Fact]
    public void Describe_AdapterThatCannotReportItsContract_IsDropped()
    {
        // Arrange
        var good = HealthyAdapter("org.test.good", "postgresql");
        var bad = Substitute.For<IDatabaseAdapter>();
        bad.Info.Returns(_ => throw new InvalidOperationException("plugin fault"));

        var loaded = new List<(IDatabaseAdapter Adapter, string Folder)>
                         {
                             (good, Path.Combine("plugins", "good")),
                             (bad, Path.Combine("plugins", "bad"))
                         };

        // Act
        var described = DatabaseAdapterCatalog.DescribeAdapters(loaded, NullLogger.Instance);

        // Assert
        described.Should().ContainSingle();
        described[0].Adapter.Should().BeSameAs(good);
        described[0].Dialect.Should().Be("postgresql");
    }

    [Fact]
    public void Describe_AdapterThatThrowsWhileReadingPlatforms_IsDropped()
    {
        // Arrange — the realistic fault: a plugin whose version table cannot be parsed.
        var good = HealthyAdapter("org.test.good", "postgresql");
        var bad = HealthyAdapter("org.test.bad", "mysql");
        bad.Platform.GetAllPlatforms(Arg.Any<string>())
            .Returns(_ => throw new FormatException("bad platform file"));

        var loaded = new List<(IDatabaseAdapter Adapter, string Folder)>
                         {
                             (bad, Path.Combine("plugins", "bad")),
                             (good, Path.Combine("plugins", "good"))
                         };

        // Act
        var described = DatabaseAdapterCatalog.DescribeAdapters(loaded, NullLogger.Instance);

        // Assert
        described.Should().ContainSingle();
        described[0].Adapter.Should().BeSameAs(good);
    }

    [Fact]
    public void Describe_HealthyAdapter_CapturesIdDialectFormatsAndPlatforms()
    {
        // Arrange
        var adapter = HealthyAdapter("org.test.postgresql", "postgresql");

        // Act
        var described = DatabaseAdapterCatalog.DescribeAdapters(
            [(adapter, Path.Combine("plugins", "postgresql"))],
            NullLogger.Instance);

        // Assert
        described.Should().ContainSingle();
        described[0].Id.Should().Be("org.test.postgresql");
        described[0].Dialect.Should().Be("postgresql");
        described[0].Formats.Should().ContainSingle(f => f.Id == "test-format");
        described[0].Platforms.Should().ContainSingle(p => p.Id == "test-platform");
    }

    private static IDatabaseAdapter HealthyAdapter(string id, string dialect)
    {
        var format = Substitute.For<IConnectionTextFormat>();
        format.Id.Returns("test-format");
        format.DetectionPriority.Returns(10);

        var platform = new PlatformEntry("test-platform", "Test Platform");

        var syntax = Substitute.For<IConnectionStringSyntax>();
        syntax.Formats.Returns(new[] { format });

        var resolver = Substitute.For<IPlatformResolver>();
        resolver.GetAllPlatforms(Arg.Any<string>()).Returns(new[] { platform });

        var adapter = Substitute.For<IDatabaseAdapter>();
        adapter.Info.Returns(new AdapterInfo(id, dialect, "*", "1.0.0"));
        adapter.Syntax.Returns(syntax);
        adapter.Platform.Returns(resolver);
        return adapter;
    }
}
