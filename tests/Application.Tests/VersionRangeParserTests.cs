using DbClone.Application.Platforms;

using FluentAssertions;

namespace Application.Tests;

public class VersionRangeParserTests
{
    [Theory]
    [InlineData("15.4", 15, 4)]
    [InlineData("16.1.2", 16, 1)]
    [InlineData("17.0", 17, 0)]
    [InlineData("14", 14, 0)]
    [InlineData("15.4 (Ubuntu 15.4-1.pgdg22.04+1)", 15, 4)]
    [InlineData("16.3 (Debian 16.3-1.pgdg120+1)", 16, 3)]
    public void ParseServerVersion_ExtractsNumericPrefix(string input, int expectedMajor, int expectedMinor)
    {
        var version = VersionRangeParser.ParseServerVersion(input);

        version.Major.Should().Be(expectedMajor);
        version.Minor.Should().Be(expectedMinor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseServerVersion_ThrowsOnEmpty(string input)
    {
        var act = () => VersionRangeParser.ParseServerVersion(input);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("15.4", "*", true)]
    [InlineData("15.4", ">=15.0", true)]
    [InlineData("14.9", ">=15.0", false)]
    [InlineData("15.0", ">=15.0", true)]
    [InlineData("17.0", "<17.0", false)]
    [InlineData("16.9", "<17.0", true)]
    [InlineData("15.4", ">=15.0 <16.0", true)]
    [InlineData("16.0", ">=15.0 <16.0", false)]
    [InlineData("14.9", ">=15.0 <16.0", false)]
    [InlineData("15.0", ">=15.0 <16.0", true)]
    [InlineData("15.9", ">=15.0 <16.0", true)]
    [InlineData("17.2", ">=17.0 <18.0", true)]
    [InlineData("18.0", ">=17.0 <18.0", false)]
    [InlineData("16.0", ">16.0", false)]
    [InlineData("16.1", ">16.0", true)]
    [InlineData("16.0", "<=16.0", true)]
    [InlineData("16.1", "<=16.0", false)]
    [InlineData("14.0", "14.*", true)]
    [InlineData("14.9", "14.*", true)]
    [InlineData("15.0", "14.*", false)]
    [InlineData("14.3", "14.x", true)]
    [InlineData("14.3", "14.X", true)]
    [InlineData("15.0", "14.x", false)]
    [InlineData("16.2", "16.*", true)]
    [InlineData("17.0", "16.*", false)]
    public void Satisfies_EvaluatesRangeCorrectly(string serverVersion, string range, bool expected)
    {
        var version = VersionRangeParser.ParseServerVersion(serverVersion);

        var result = VersionRangeParser.Satisfies(version, range);

        result.Should().Be(expected, $"version {serverVersion} vs range '{range}'");
    }

    [Fact]
    public void Satisfies_MajorOnlyVersion_MatchesRange()
    {
        // PostgreSQL 10+ uses major-only versioning (e.g. "15" means 15.x)
        var version = VersionRangeParser.ParseServerVersion("15");

        VersionRangeParser.Satisfies(version, ">=15.0 <16.0").Should().BeTrue();
        VersionRangeParser.Satisfies(version, ">=16.0 <17.0").Should().BeFalse();
    }

    [Theory]
    [InlineData("*", ">=15.0", true)]
    [InlineData("*", "14.*", true)]
    [InlineData(">=15.0 <16.0", ">=15.5 <17.0", true)]
    [InlineData(">=15.0 <16.0", ">=16.0 <17.0", false)]
    [InlineData("14.*", ">=14.0 <15.0", true)]
    [InlineData("14.*", "15.*", false)]
    [InlineData(">=15.0 <16.0", ">=17.0 <18.0", false)]
    [InlineData("<16.0", ">=15.0", true)]
    public void RangesOverlap_DetectsCorrectly(string range1, string range2, bool expected)
    {
        VersionRangeParser.RangesOverlap(range1, range2).Should().Be(expected,
            $"'{range1}' vs '{range2}'");
    }
}
