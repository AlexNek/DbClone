using CanonicalModel.Interchange;

using DbAdapter.Definitions;

using DatabaseInterchange.Format;
using DatabaseInterchange.Report;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// A store is repeatable: the second one over the same folder replaces the summary instead of leaving the
/// first beside it. The report describes the catalog that is on disk now, so an older text next to it
/// would be a wrong statement about the user's data — and a scratch file left behind would read as a
/// write that is still running.
/// </summary>
public sealed class CatalogReportOverwriteTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeReportOverwrite_" + Guid.NewGuid().ToString("N"));

    private readonly CatalogReportWriter _sut = new();

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task SecondStore_LeavesExactlyOneReport_DescribingTheSecondExport()
    {
        // Arrange — a first store whose catalog names a table the second one does not carry.
        await StoreAsync(FirstExport(), rowsPerTable: 1, FirstStamp);
        var firstText = await File.ReadAllTextAsync(ReportPath());

        // Act
        await StoreAsync(SecondExport(), rowsPerTable: 5, SecondStamp);

        // Assert — one report, at one name: neither the superseded text nor its scratch file is kept.
        var reportFiles = Directory.GetFiles(_catalogPath, "summary*", SearchOption.AllDirectories);
        reportFiles.Should().ContainSingle("a second store replaces the report rather than adding to it");
        Path.GetFileName(reportFiles[0]).Should().Be(CatalogReportWriter.ReportFileName);

        // Assert — what is on disk is the second export's account alone: the object the second store did
        // not capture is gone from the text, as is the first store's timestamp.
        var secondText = await File.ReadAllTextAsync(ReportPath());
        firstText.Should().Contain("audit_log", "the first export captured the table this asserts against");
        secondText.Should().NotContain("audit_log", "no line of the superseded report survives");
        secondText.Should().Contain("payments");
        secondText.Should().NotContain(
            Stamp(FirstStamp), $"the reader must not be shown the previous store's instant: {secondText[..80]}…");
        secondText.Should().Contain(Stamp(SecondStamp));
    }

    [Fact]
    public async Task SecondStore_ReplacesTheReportEvenWhenTheFirstWasHandWritten()
    {
        // Arrange — text the user put in the catalog folder themselves, in the report's name.
        await StoreAsync(FirstExport(), rowsPerTable: 1, FirstStamp);
        await File.WriteAllBytesAsync(ReportPath(), "# my own notes\n"u8.ToArray());

        // Act
        await StoreAsync(SecondExport(), rowsPerTable: 1, SecondStamp);

        // Assert — D4.1.1 keeps a report through a failed move; a move that succeeds replaces the file, and
        // the catalog's own text is what a reader gets afterwards.
        var text = await File.ReadAllTextAsync(ReportPath());
        text.Should().NotContain("my own notes");
        text.Should().StartWith($"# Catalog summary — {ReportModel.Database}");
        Directory.GetFiles(_catalogPath, "summary*", SearchOption.AllDirectories).Should().HaveCount(1);
    }

    /// <summary>The stamps of the two stores, whole seconds apart — the only line FR2 says may differ.</summary>
    private static readonly DateTimeOffset FirstStamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly DateTimeOffset SecondStamp = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static string Stamp(DateTimeOffset exportedAt) =>
        exportedAt.ToString(ReportBudget.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Two tables, one of which the second export leaves out.</summary>
    private static InterchangeSnapshot FirstExport() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            ReportModel.KeyedTable("public", "audit_log"));

    private static InterchangeSnapshot SecondExport() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            ReportModel.KeyedTable("public", "payments") with
            {
                ForeignKeys =
                [
                    ReportModel.Reference(
                        "public", "payments", "fk_payment_customer", "public", "customers",
                        ["customer_id"], ["id"]),
                ],
            });

    /// <summary>
    /// One store over the folder: the real writer closes the catalog, then the real report writer lands the
    /// summary from the facts the writer finished with. <c>overwrite: true</c> is how the exporter itself
    /// re-uses a directory.
    /// </summary>
    private async Task StoreAsync(
        InterchangeSnapshot snapshot, int rowsPerTable, DateTimeOffset exportedAt)
    {
        await using var writer = await CatalogWriter.CreateInterchangeAsync(
            _catalogPath, ReportModel.Producer, ReportModel.Dialect, overwrite: true);

        await writer.WriteSectionAsync("schemas", [ReportModel.Schema("public")]);
        await writer.WriteSectionAsync("tables", snapshot.Tables);
        await writer.WriteSectionAsync("columns", [.. snapshot.Tables.SelectMany(t => t.Columns)]);

        foreach (var table in snapshot.Tables)
        {
            await writer.WriteTableAsync(
                table, "public", Rows(rowsPerTable), new ExportOptions { MaxRowsPerChunk = 2 });
        }

        await writer.CompleteAsync();

        var statistics = ReportModel.Statistics(
            snapshot, writer.TableStatistics, exportedAt: exportedAt);
        var issues = new List<InterchangeIssue>();

        var path = await _sut.WriteAsync(
            _catalogPath, statistics, ReportModel.MapOf(snapshot), issues, CancellationToken.None);

        path.Should().Be(CatalogReportWriter.ReportFileName);
        issues.Should().BeEmpty("the folder was writable throughout; nothing here is a failure case");
    }

    private static async IAsyncEnumerable<DataValue[]> Rows(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return new DataValue[]
            {
                DataValue.FromText(ELogicalType.Integer32, index.ToString()),
                DataValue.FromText(ELogicalType.String, $"row {index}"),
            };
        }

        await Task.CompletedTask;
    }

    private string ReportPath() => Path.Combine(_catalogPath, CatalogReportWriter.ReportFileName);
}
