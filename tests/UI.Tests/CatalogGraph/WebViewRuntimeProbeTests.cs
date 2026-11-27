using DbClone.UI.Services.Relationship;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// D4: the probe is what decides between the app's window and the browser, so "no runtime" has to be a
/// quiet answer rather than an exception — including the case where the loader reports nothing at all.
/// </summary>
/// <remarks>
/// The tests never touch a real install: they judge the version the loader would report, which is the
/// only input the probe has.
/// </remarks>
public sealed class WebViewRuntimeProbeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_version_that_is_absent_reads_as_unavailable(string? version) =>
        WebViewRuntimeProbe.IsVersionUsable(version).Should().BeFalse();

    [Fact]
    public void An_installed_runtime_reads_as_available() =>
        WebViewRuntimeProbe.IsVersionUsable("131.0.2903.86").Should().BeTrue();
}
