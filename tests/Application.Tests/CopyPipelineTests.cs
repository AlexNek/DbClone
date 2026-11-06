using DbClone.Application.DTOs;
using DbClone.Application.Copy;
using DbAdapter.Definitions;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using CanonicalModel.Enums;
using CanonicalModel.DTOs;
using CanonicalModel.Models;

namespace Application.Tests;

public class CopyPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceled()
    {
        var stage = new TestStage(ECopyStage.CopyData, 50, true, TimeSpan.FromSeconds(5));
        var pipeline = new CopyPipeline([stage], NullLogger<CopyPipeline>.Instance);
        var context = CreateContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => pipeline.ExecuteAsync(context, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_CriticalStageFailure_Aborts()
    {
        var stages = new ICopyStage[]
                         {
                             new TestStage(
                                 ECopyStage.Connect,
                                 10,
                                 false), // Order <= 30 is critical
                             new TestStage(ECopyStage.DetectCapabilities, 20, true)
                         };
        var pipeline = new CopyPipeline(stages, NullLogger<CopyPipeline>.Instance);
        var context = CreateContext();

        var result = await pipeline.ExecuteAsync(context);

        result.Success.Should().BeFalse();
        result.StageResults.Should().HaveCount(1); // Only the failed stage ran
    }

    [Fact]
    public async Task ExecuteAsync_MultipleStages_ExecutesInOrder()
    {
        var stages = new ICopyStage[]
                         {
                             new TestStage(ECopyStage.CreateTables, 20, true),
                             new TestStage(ECopyStage.CreateSchemas, 10, true)
                         };
        var pipeline = new CopyPipeline(stages, NullLogger<CopyPipeline>.Instance);
        var context = CreateContext();

        var result = await pipeline.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        result.StageResults.Should().HaveCount(2);
        result.StageResults[0].StageName.Should().Be(ECopyStage.CreateSchemas);
        result.StageResults[1].StageName.Should().Be(ECopyStage.CreateTables);
    }

    [Fact]
    public async Task ExecuteAsync_NoStages_ReturnsSuccess()
    {
        var pipeline = new CopyPipeline([], NullLogger<CopyPipeline>.Instance);
        var context = CreateContext();

        var result = await pipeline.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        result.StageResults.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SingleStage_Executes()
    {
        var stage = new TestStage(ECopyStage.Validate, 10, true);
        var pipeline = new CopyPipeline([stage], NullLogger<CopyPipeline>.Instance);
        var context = CreateContext();

        var result = await pipeline.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        result.StageResults.Should().HaveCount(1);
        result.StageResults[0].StageName.Should().Be(ECopyStage.Validate);
    }

    [Fact]
    public async Task ExecuteAsync_StageFailure_ContinuesForNonCritical()
    {
        var stages = new ICopyStage[]
                         {
                             new TestStage(ECopyStage.CreateViews, 100, false),
                             new TestStage(ECopyStage.CreateTriggers, 110, true)
                         };
        var pipeline = new CopyPipeline(stages, NullLogger<CopyPipeline>.Instance);
        var context = CreateContext();

        var result = await pipeline.ExecuteAsync(context);

        // Non-critical stage failure continues
        result.StageResults.Should().HaveCount(2);
    }

    private static CopyContext CreateContext()
    {
        var request = new CopyRequest(
            new DatabaseTarget(
                "postgresql",
                "Host=localhost;Port=5432;Database=source;Username=user;Password=pass"),
            new DatabaseTarget(
                "postgresql",
                "Host=localhost;Port=5432;Database=dest;Username=user;Password=pass"),
            new DatabaseModel(
                DatabaseName: "source",
                ServerVersion: "PostgreSQL 16.0",
                Schemas: [],
                Tables: [],
                Views: [],
                MaterializedViews: [],
                Sequences: [],
                Enums: [],
                Domains: [],
                CompositeTypes: [],
                Functions: [],
                Triggers: [],
                Policies: [],
                Publications: [],
                Subscriptions: [],
                Extensions: []),
            new CopyOptions());
        return new CopyContext { Request = request };
    }

    private sealed class TestStage : ICopyStage
    {
        private readonly TimeSpan _delay;

        private readonly bool _success;

        public ECopyStage Name { get; }

        public int Order { get; }

        public TestStage(ECopyStage name, int order, bool success, TimeSpan delay = default)
        {
            Name = name;
            Order = order;
            _success = success;
            _delay = delay;
        }

        public async Task<StageResult> ExecuteAsync(
            CopyContext context,
            CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken);

            return new StageResult(Name, _success, TimeSpan.Zero, _success ? 1 : 0, []);
        }
    }
}
