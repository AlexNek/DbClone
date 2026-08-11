using DbClone.Application.Models;
using DbClone.Application.TableFilter;

using FluentAssertions;

namespace Application.Tests;

public class TableSelectionSpecTests
{
    [Fact]
    public void All_IsNotActive()
    {
        TableSelectionSpec.All.IsActive.Should().BeFalse();
    }

    [Fact]
    public void EnabledWithEmptyExclusions_NormalizesToInactive()
    {
        var spec = new TableSelectionSpec(true, new HashSet<TableId>());

        spec.IsActive.Should().BeFalse();
    }

    [Fact]
    public void DisabledWithExclusions_IsNotActive()
    {
        var spec = new TableSelectionSpec(false, new HashSet<TableId> { new("public", "orders") });

        spec.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Excluding_CreatesActiveSpec()
    {
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        spec.IsEnabled.Should().BeTrue();
        spec.IsActive.Should().BeTrue();
        spec.ExcludedTables.Should().HaveCount(1);
    }

    [Fact]
    public void IsExcluded_IsCaseInsensitive()
    {
        var spec = TableSelectionSpec.Excluding([new TableId("public", "Orders")]);

        spec.IsExcluded(new TableId("PUBLIC", "orders")).Should().BeTrue();
    }

    [Fact]
    public void IsExcluded_ReturnsFalseForIncludedTable()
    {
        var spec = TableSelectionSpec.Excluding([new TableId("public", "orders")]);

        spec.IsExcluded(new TableId("public", "customers")).Should().BeFalse();
    }
}

public class TableIdTests
{
    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        new TableId("Public", "ORDERS").Should().Be(new TableId("public", "orders"));
        new TableId("Public", "ORDERS").GetHashCode()
            .Should().Be(new TableId("public", "orders").GetHashCode());
    }

    [Fact]
    public void FullName_JoinsSchemaAndName()
    {
        new TableId("public", "orders").FullName.Should().Be("public.orders");
    }
}
