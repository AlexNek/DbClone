using FluentAssertions;

namespace UI.Tests.Services;

public sealed class AppInfoTests
{
    [Fact]
    public void ProductName_is_not_empty()
    {
        AppInfo.ProductName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ProductName_equals_DbClone()
    {
        AppInfo.ProductName.Should().Be("DbClone");
    }

    [Fact]
    public void Version_is_not_empty()
    {
        AppInfo.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Version_matches_semver_pattern()
    {
        // Should match "1.2.3" or "1.2.3 (build 44)" or "0.0.0-dev"
        AppInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    public void FullTitle_contains_product_name()
    {
        AppInfo.FullTitle.Should().Contain("DbClone");
    }

    [Fact]
    public void FullTitle_contains_version()
    {
        // The version portion after "v" must not be empty
        AppInfo.FullTitle.Should().MatchRegex(@"v\d+\.\d+\.\d+");
    }

    [Fact]
    public void FullTitle_has_expected_format()
    {
        // "DbClone — PostgreSQL Database Copy Tool v1.2.3..."
        AppInfo.FullTitle.Should().StartWith("DbClone — PostgreSQL Database Copy Tool v");
    }
}
