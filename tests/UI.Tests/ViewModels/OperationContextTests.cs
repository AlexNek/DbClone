using DbClone.Application.Interfaces;
using DbClone.UI.ViewModels;

using FluentAssertions;

using NSubstitute;

using UI.Tests.Fakes;

namespace UI.Tests.ViewModels;

/// <summary>
/// Unit tests for OperationContext — the shared coordination state
/// (connections + busy lock + log pane layout only).
/// </summary>
public class OperationContextTests
{
    [Fact]
    public void Initial_state_has_sensible_defaults()
    {
        var ctx = CreateContext();

        ctx.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void IsBusy_raises_PropertyChanged()
    {
        var ctx = CreateContext();
        var raised = false;
        ctx.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OperationContext.IsBusy))
                    raised = true;
            };

        ctx.IsBusy = true;

        raised.Should().BeTrue();
    }

    [Fact]
    public void Source_and_Destination_are_exposed()
    {
        var ctx = CreateContext();

        ctx.Source.Should().NotBeNull();
        ctx.Source.Label.Should().Be("Source");
        ctx.Destination.Should().NotBeNull();
        ctx.Destination.Label.Should().Be("Destination");
    }

    private static OperationContext CreateContext()
    {
        var connectionStringService = Substitute.For<IConnectionStringService>();
        var maintenanceProvider = Substitute.For<IDatabaseMaintenanceProvider>();

        var source =
            new ConnectionViewModel(connectionStringService, maintenanceProvider, TestPlatformResolver.Create())
                {
                    Label = "Source"
                };
        var destination =
            new ConnectionViewModel(connectionStringService, maintenanceProvider, TestPlatformResolver.Create())
                {
                    Label = "Destination"
                };

        return new OperationContext(source, destination);
    }
}
