using DbClone.UI.Models;

using FluentAssertions;

namespace UI.Tests.Models;

/// <summary>
/// Unit tests for LogPaneSplitterPosition — per-workflow remembered splitter height.
/// </summary>
public class LogPaneSplitterPositionTests
{
    [Fact]
    public void Capture_ignores_collapsed_zero_height()
    {
        var position = new LogPaneSplitterPosition();
        position.Capture(350);

        position.Capture(0);

        position.Height.Should().Be(350);
    }

    [Fact]
    public void Capture_ignores_negative_height()
    {
        var position = new LogPaneSplitterPosition();
        position.Capture(350);

        position.Capture(-5);

        position.Height.Should().Be(350);
    }

    [Fact]
    public void Capture_stores_positive_height()
    {
        var position = new LogPaneSplitterPosition();

        position.Capture(321);

        position.Height.Should().Be(321);
    }

    [Fact]
    public void Height_defaults_to_DefaultHeight()
    {
        var position = new LogPaneSplitterPosition();

        position.Height.Should().Be(LogPaneSplitterPosition.DefaultHeight);
    }

    [Fact]
    public void Restore_accepts_valid_saved_height()
    {
        var position = new LogPaneSplitterPosition();

        position.Restore(450);

        position.Height.Should().Be(450);
    }

    [Fact]
    public void Restore_falls_back_to_default_for_too_large_value()
    {
        var position = new LogPaneSplitterPosition();

        position.Restore(5000);

        position.Height.Should().Be(LogPaneSplitterPosition.DefaultHeight);
    }

    [Fact]
    public void Restore_falls_back_to_default_for_too_small_value()
    {
        var position = new LogPaneSplitterPosition();

        position.Restore(50);

        position.Height.Should().Be(LogPaneSplitterPosition.DefaultHeight);
    }

    [Fact]
    public void Two_instances_are_independent()
    {
        var copy = new LogPaneSplitterPosition();
        var compare = new LogPaneSplitterPosition();

        copy.Capture(300);
        compare.Capture(500);

        copy.Height.Should().Be(300);
        compare.Height.Should().Be(500);
    }
}
