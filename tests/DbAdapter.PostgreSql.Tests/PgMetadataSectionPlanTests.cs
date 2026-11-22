using DbAdapter.Definitions;

using DbAdapter.PostgreSql.Metadata;

using FluentAssertions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// Pins the scope-to-section decision the metadata read gates on: <see cref="EMetadataScope.Everything"/>
/// reads all twenty sections, and <see cref="EMetadataScope.Structure"/> reads exactly the nine a
/// picture of the schema is drawn from — the eleven it skips are the twelve statements the live
/// relationship read no longer waits for.
/// </summary>
public class PgMetadataSectionPlanTests
{
    private static readonly EPgMetadataSection[] StructureSections =
    [
        EPgMetadataSection.ServerFacts,
        EPgMetadataSection.Extensions,
        EPgMetadataSection.Schemas,
        EPgMetadataSection.Tables,
        EPgMetadataSection.Columns,
        EPgMetadataSection.Indexes,
        EPgMetadataSection.ForeignKeys,
        EPgMetadataSection.Checks,
        EPgMetadataSection.UniqueKeys,
    ];

    public static IEnumerable<object[]> EverySection() =>
        Enum.GetValues<EPgMetadataSection>().Select(section => new object[] { section });

    [Theory]
    [MemberData(nameof(EverySection))]
    public void Reads_Everything_ReadsEverySection(EPgMetadataSection section) =>
        PgMetadataSectionPlan.Reads(EMetadataScope.Everything, section).Should().BeTrue();

    [Theory]
    [MemberData(nameof(EverySection))]
    public void Reads_Structure_ReadsExactlyWhatThePictureDraws(EPgMetadataSection section) =>
        PgMetadataSectionPlan.Reads(EMetadataScope.Structure, section)
            .Should().Be(StructureSections.Contains(section));

    [Fact]
    public void Reads_Structure_SkipsTheElevenSectionsThePictureNeverUses() =>
        Enum.GetValues<EPgMetadataSection>()
            .Count(section => !PgMetadataSectionPlan.Reads(EMetadataScope.Structure, section))
            .Should().Be(11);

    [Fact]
    public void Reads_Structure_KeepsExtensionDiscoveryAndSearchPathWidening()
    {
        // format_type and pg_get_constraintdef qualify what is not on the search path, and the
        // panel prints both — the preparation the picture's wording depends on is not optional.
        PgMetadataSectionPlan.Reads(EMetadataScope.Structure, EPgMetadataSection.Extensions)
            .Should().BeTrue();
    }
}
