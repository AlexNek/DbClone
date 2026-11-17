using System.Globalization;
using System.Text;

using CanonicalModel.Interchange;

using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// FR8 and D9: the same facts must print the same bytes. The file is meant to be committed beside a
/// catalog, so a format coin-flip — a culture-sensitive decimal, an ordering that follows dictionary
/// insertion — would put noise in every user's <c>git diff</c> and make the report untrustworthy as a
/// record of what changed.
/// </summary>
public sealed class CatalogReportDeterminismTests
{
    [Fact]
    public void Document_ForTheSameSnapshot_Twice_IsByteIdentical()
    {
        // Arrange
        var snapshot = MixedRelations();

        // Act
        var first = Encoding.UTF8.GetBytes(ReportModel.Document(snapshot));
        var second = Encoding.UTF8.GetBytes(ReportModel.Document(snapshot));

        // Assert — no dictionary ordering, no floating state, no re-read of the world.
        second.Should().Equal(first);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("cs-CZ")]
    [InlineData("en-US")]
    public void Document_UnderAnyCurrentCulture_IsByteIdenticalToTheInvariantOne(string culture)
    {
        // Arrange — en-US is the culture the machine is already running under, so it acts as the reference
        // rather than as a privileged case: what is being asserted is that no other culture moves a byte.
        var reference = RenderUnderCulture("en-US");

        // Act
        var underCulture = RenderUnderCulture(culture);

        // Assert (D9.1) — German writes 1234.5 as "1234,5" and would put a comma inside every percentage
        // and share cell if any number were formatted through CurrentCulture.
        underCulture.Should().Be(reference, $"{culture} must not reach the text at all");
        Encoding.UTF8.GetBytes(underCulture).Should().Equal(Encoding.UTF8.GetBytes(reference));
    }

    [Fact]
    public void Document_ForTheSameTablesInShuffledInsertOrder_IsByteIdentical()
    {
        // Arrange — insertion order is what a live database's catalog view decides, and it is not a fact
        // about the schema. Every listing therefore sorts ordinal, so the text cannot inherit it.
        var ordered = ReportModel.Snapshot([.. WideTables()]);
        var shuffled = ReportModel.SnapshotFrom([.. WideTables().Reverse()]);
        var alternated = ReportModel.SnapshotFrom(
            [.. WideTables().OrderBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.Ordinal).Reverse()]);

        // Act
        var document = ReportModel.Document(ordered);

        // Assert
        ReportModel.Document(shuffled).Should().Be(document);
        ReportModel.Document(alternated).Should().Be(document);
    }

    [Fact]
    public void Document_TwoRunsApartInTimeOnly_DifferOnTheTimestampLineAndNowhereElse()
    {
        // Arrange — the criterion the user's diff depends on: a re-store of an unchanged database is a
        // one-line change, and that line is the one FR2 says must state when the catalog was made.
        var snapshot = MixedRelations();
        var earlier = ReportModel.Document(snapshot, ReportModel.Statistics(
            snapshot, exportedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        var later = ReportModel.Document(snapshot, ReportModel.Statistics(
            snapshot, exportedAt: new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero)));

        // Assert
        var differences = Lines(earlier).Select((line, index) => (line, index))
            .Where(pair => pair.line != later.Split('\n')[pair.index])
            .ToList();
        differences.Should().ContainSingle();
        differences[0].line.Should().Be("Produced by DbClone-test · exported 2026-01-02T03:04:05Z");
        later.Split('\n')[differences[0].index]
            .Should().Be("Produced by DbClone-test · exported 2026-06-07T08:09:10Z");
        differences[0].index.Should().Be(1, "the timestamp is the second line of the header, not a value buried in a table");
    }

    [Fact]
    public void Document_AcrossParallelRenders_IsByteIdentical()
    {
        // Arrange — the renderers hold no shared mutable state; a run that varies under parallelism would
        // make the committed file depend on thread scheduling.
        var snapshot = MixedRelations();
        var reference = ReportModel.Document(snapshot);

        // Act
        var variants = new string[8];
        Parallel.For(0, variants.Length, index => variants[index] = ReportModel.Document(snapshot));

        // Assert
        variants.Should().OnlyContain(text => text == reference);
    }

    /// <summary>Two schemas, a cycle, a duplicate pair, a self reference, an external target and a view reference.</summary>
    private static InterchangeSnapshot MixedRelations()
    {
        var customers = ReportModel.KeyedTable("billing", "customers");
        var invoices = ReportModel.KeyedTable("billing", "invoices") with
        {
            ForeignKeys =
            [
                ReportModel.Reference("billing", "invoices", "fk_invoice_customer", "billing", "customers", ["customer_id"], ["id"]),
                ReportModel.Reference("billing", "invoices", "fk_invoice_payer", "billing", "customers", ["payer_id"], ["id"]),
                ReportModel.Reference("billing", "invoices", "fk_invoice_self", "billing", "invoices", ["previous_id"], ["id"]),
                ReportModel.Reference("billing", "invoices", "fk_invoice_region", "public", "regions", ["region_id"], ["id"]),
            ],
        };
        var payments = ReportModel.Table("billing", "payments",
            ReportModel.Column("billing", "payments", "id", nullable: false),
            ReportModel.Column("billing", "payments", "invoice_id", nullable: false)) with
        {
            PrimaryKey = ReportModel.PrimaryKey("billing", "payments", "id"),
            ForeignKeys =
            [
                ReportModel.Reference("billing", "payments", "fk_payment_invoice", "billing", "invoices", ["invoice_id"], ["id"]),
            ],
        };
        var regions = ReportModel.KeyedTable("public", "regions");

        // One view and the lineage edge that reads it, so the Lineage section is exercised too: it comes
        // from the snapshot's relationship list, which is where a captured catalog states it (§5.8).
        return ReportModel.Snapshot(customers, invoices, payments, regions) with
        {
            Views =
            [
                new ViewDefinition
                {
                    Ref = ObjectRef.View("public", "region_summary"),
                    Name = "region_summary",
                    SchemaRef = ObjectRef.Schema("public"),
                },
            ],
            Relationships =
            [
                new Relationship
                {
                    DependentRef = ObjectRef.View("public", "region_summary"),
                    DependencyRef = ObjectRef.Table("public", "regions"),
                    Type = "viewReference",
                },
            ],
        };
    }

    private static IEnumerable<TableDefinition> WideTables() =>
        [.. Enumerable.Range(0, 30).Select(index => ReportModel.KeyedTable("public", $"table_{index:0000}"))];

    private static string[] Lines(string document) => document.Split('\n');

    /// <summary>Renders one document on a thread whose <see cref="CultureInfo.CurrentCulture"/> is set.</summary>
    private static string RenderUnderCulture(string culture)
    {
        string? document = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
                document = ReportModel.Document(MixedRelations());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        thread.Join();

        failure.Should().BeNull($"rendering under {culture} must not fail");
        return document!;
    }
}
