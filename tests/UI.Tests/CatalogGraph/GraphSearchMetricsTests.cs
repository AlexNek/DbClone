using DbClone.UI.Services.GraphLayout;
using FluentAssertions;

namespace UI.Tests.CatalogGraph;

public sealed class GraphSearchMetricsTests
{
    [Fact]
    public void Observations_record_actual_peaks_and_moves_without_spending_evaluations()
    {
        var budget = new GraphEvaluationBudget(10);

        budget.Metrics.ObserveCandidates(40);
        budget.Metrics.ObserveCandidates(29);
        budget.Metrics.ObserveConflictSet(8);
        budget.Metrics.ObserveConflictSet(2);
        budget.Metrics.ObserveRepairMove();

        budget.Used.Should().Be(0);
        budget.Metrics.PeakRouteCandidates.Should().Be(40);
        budget.Metrics.PeakConflictSetSize.Should().Be(8);
        budget.Metrics.ConflictSets.Should().Be(2);
        budget.Metrics.RepairMoves.Should().Be(1);
    }

    [Fact]
    public void Child_budgets_share_observations_but_keep_their_evaluation_limits()
    {
        var parent = new GraphEvaluationBudget(10);
        var child = new GraphEvaluationBudget(1, parent.Metrics);

        child.Metrics.ObserveCandidates(37);
        child.TrySpend().Should().BeTrue();
        child.TrySpend().Should().BeFalse();
        parent.Add(child.Used);

        child.Metrics.Should().BeSameAs(parent.Metrics);
        parent.Metrics.PeakRouteCandidates.Should().Be(37);
        parent.Used.Should().Be(1);
        parent.IsExhausted.Should().BeFalse();
    }
}
