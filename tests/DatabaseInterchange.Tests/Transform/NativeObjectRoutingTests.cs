using CanonicalModel.Interchange;

using DbAdapter.Definitions;
using DbAdapter.Definitions.Interchange;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Transform;

/// <summary>
/// The table the import splits the catalog's native file by. It owns exactly one decision — which
/// stage creates which kind, and where a kind stands inside that stage — and these tests are what catch
/// a row moved or dropped, since both mistakes stay hidden in a catalog that happens to hold nothing of
/// the kind concerned.
/// </summary>
public sealed class NativeObjectRoutingTests
{
    [Theory]
    [InlineData(EObjectKind.Enum)]
    [InlineData(EObjectKind.CompositeType)]
    [InlineData(EObjectKind.RangeType)]
    [InlineData(EObjectKind.Domain)]
    [InlineData(EObjectKind.Collation)]
    [InlineData(EObjectKind.Operator)]
    [InlineData(EObjectKind.Cast)]
    public void StageFor_TypeKinds_AnswerTheTypeStage(EObjectKind kind) =>
        NativeObjectRouting.StageFor(kind).Should().Be(SchemaStage.Types);

    [Fact]
    public void StageFor_PublicationAndSubscription_AnswerStagesOfTheirOwn()
    {
        // These kinds have a stage of their own so that no other stage is the place that says no to them:
        // a handler that creates types should not also be the code that decides what happens to a
        // publication, which is what made adding one an edit to the type handler.
        NativeObjectRouting.StageFor(EObjectKind.Publication).Should().Be(SchemaStage.Publications);
        NativeObjectRouting.StageFor(EObjectKind.Subscription).Should().Be(SchemaStage.Subscriptions);
    }

    [Fact]
    public void StageFor_KindsNamingTables_AnswerAfterTheTableStage()
    {
        // A publication lists tables and a subscription reads a publication, so neither can be created
        // while the tables are still missing. The importer splits its two passes on this number.
        ((int)SchemaStage.Publications).Should().BeGreaterThan((int)SchemaStage.Tables);
        ((int)SchemaStage.Subscriptions).Should().BeGreaterThan((int)SchemaStage.Publications);
    }

    [Theory]
    [InlineData(EObjectKind.Table)]
    [InlineData(EObjectKind.View)]
    [InlineData(EObjectKind.Sequence)]
    [InlineData(EObjectKind.Trigger)]
    public void StageFor_KindsTheStructuredSectionsCarry_AnswerNoStage(EObjectKind kind)
    {
        // The native file is for what the sections have no home for. A table appearing there would give
        // the catalog two records of one object, and the honest answer is to refuse it rather than choose
        // between the two.
        NativeObjectRouting.StageFor(kind).Should().BeNull();
    }

    [Fact]
    public void RankInStage_OrdersTypesByWhatOneCanBeDeclaredOn()
    {
        // A composite holds an enum's column and a domain rests on either, so the declarations have to be
        // run in this order. The file records no ids to follow, which makes this the only order it answers.
        var ordered = new[]
            {
                EObjectKind.Cast,
                EObjectKind.Domain,
                EObjectKind.Enum,
                EObjectKind.CompositeType,
                EObjectKind.RangeType,
            }
            .OrderBy(NativeObjectRouting.RankInStage)
            .ToArray();

        ordered.Should().Equal(
            EObjectKind.Enum,
            EObjectKind.CompositeType,
            EObjectKind.RangeType,
            EObjectKind.Domain,
            EObjectKind.Cast);
    }

    [Fact]
    public void RankInStage_KindsWhoseStageHoldsNoOrder_RankAlike()
    {
        NativeObjectRouting.RankInStage(EObjectKind.Publication)
            .Should().Be(NativeObjectRouting.RankInStage(EObjectKind.Subscription));
    }
}
