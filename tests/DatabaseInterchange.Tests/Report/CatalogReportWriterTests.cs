using System.Reflection;
using System.Text;

using CanonicalModel.Interchange;

using DatabaseInterchange.Format;
using DatabaseInterchange.Report;
using DatabaseInterchange.Script;

using FluentAssertions;

using Xunit;

namespace DatabaseInterchange.Tests.Report;

/// <summary>
/// The one <c>System.IO</c> call site of the feature (D4, D4.1): the report lands in the catalog root by
/// content-then-move, and a filesystem that refuses it costs the catalog nothing.
/// </summary>
/// <remarks>
/// <para>
/// The failures are forced by holding a file open with <see cref="FileShare.None"/> rather than by
/// substituting a filesystem, so what is being asserted is the real Windows response to a busy
/// destination — the case D4.1.2 names (an editor holding <c>summary.md</c>).
/// </para>
/// <para>
/// The scratch file is deliberately <em>not</em> asserted to be gone after a failure: D4.1.3 makes its
/// cleanup best-effort, so §7 asks only that no report was produced.
/// </para>
/// </remarks>
public sealed class CatalogReportWriterTests : IDisposable
{
    private readonly string _catalogPath = Path.Combine(
        Path.GetTempPath(),
        "DbInterchangeReportWriter_" + Guid.NewGuid().ToString("N"));

    private readonly CatalogReportWriter _sut = new();

    public void Dispose()
    {
        if (Directory.Exists(_catalogPath))
        {
            Directory.Delete(_catalogPath, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_LandsTheReportInTheCatalogRoot_AndLeavesTheManifestUntouched()
    {
        // Arrange — a real catalog, so the report is written beside the files it describes (FR15).
        await CreateCatalogAsync();
        var manifest = Path.Combine(_catalogPath, InterchangeFormat.ManifestFileName);
        var manifestBefore = await File.ReadAllBytesAsync(manifest);
        var issues = new List<InterchangeIssue>();

        // Act
        var relativePath = await WriteAsync(issues);

        // Assert (FR1, FR15) — the name is relative, the file is in the root, and the content identity
        // the manifest records is exactly what it was: the report is not in it and cannot disturb it.
        relativePath.Should().Be(CatalogReportWriter.ReportFileName);
        ReportExists().Should().BeTrue();
        TempExists().Should().BeFalse("the move leaves no scratch file behind when the disk cooperates");
        (await File.ReadAllBytesAsync(manifest)).Should().Equal(manifestBefore);
        issues.Should().BeEmpty();

        var manifestText = await File.ReadAllTextAsync(manifest);
        manifestText.Should().NotContain("summary", "the report is outside the catalog's content identity (D8)");
    }

    [Fact]
    public async Task WriteAsync_WritesTheRenderedTextByteForByte_NoBomNewlineOnlyFinalNewline()
    {
        // Arrange
        await CreateCatalogAsync();
        var snapshot = Fixture();
        var expected = new MarkdownCatalogReportRenderer().Render(
            ReportModel.Statistics(snapshot), ReportModel.MapOf(snapshot));

        // Act
        await WriteAsync(new List<InterchangeIssue>());

        // Assert (D9.2) — the bytes are the string: UTF-8 with no identifier, \n only, and the document's
        // own trailing newline present, so a committed file never shows a "no newline at end of file" hunk.
        var bytes = await File.ReadAllBytesAsync(ReportPath());
        bytes.Should().Equal(Encoding.UTF8.GetBytes(expected));
        bytes[..3].Should().NotEqual(
            new byte[] { 0xEF, 0xBB, 0xBF },
            "a BOM would put a character no markdown reader asked for at the top");
        bytes.Should().NotContain((byte)'\r');
        bytes[^1].Should().Be((byte)'\n', "a committed file must not show a missing-final-newline hunk");
    }

    [Fact]
    public async Task WriteAsync_WhenTheMoveFails_KeepsTheExistingReportAndRecordsOneWarning()
    {
        // Arrange — a report the user wrote by hand, held open so the move is refused (D4.1.2).
        await CreateCatalogAsync();
        var handWritten = Utf8Bytes("# my own notes\n");
        await File.WriteAllBytesAsync(ReportPath(), handWritten);
        var issues = new List<InterchangeIssue>();

        string? relativePath;
        using (var held = OpenWithExclusiveLock(ReportPath()))
        {
            relativePath = await WriteAsync(issues);
        }

        // Assert (FR13, D4.1.1) — nothing was retried, deleted or written to the target directly, so the
        // text that was there survives byte for byte: the target is never opened for writing.
        relativePath.Should().BeNull();
        (await File.ReadAllBytesAsync(ReportPath())).Should().Equal(handWritten);
        issues.Should().ContainSingle("a refused report is one issue, not a retry loop");
        issues[0].Code.Should().Be(CatalogReportWriter.WriteFailureCode);
        issues[0].Severity.Should().Be(EIssueSeverity.Warning);
        issues[0].Message.Should().Contain(ReportPath(), "the user is told which folder refused");
    }

    [Fact]
    public async Task WriteAsync_WhenTheContentWriteFails_ProducesNoReportAndToleratesTheLockedScratchFile()
    {
        // Arrange — no report at all, and a scratch name the filesystem will not give up: the writer's
        // own FileMode.Create is refused before a byte is written (D4.1.3).
        await CreateCatalogAsync();
        var leftOver = Utf8Bytes("left by an earlier interrupted store\n");
        await File.WriteAllBytesAsync(TempPath(), leftOver);
        var issues = new List<InterchangeIssue>();

        string? relativePath;
        using (var held = OpenWithExclusiveLock(TempPath()))
        {
            relativePath = await WriteAsync(issues);
        }

        // Assert — the target was never created, so a reader cannot see a half report; the scratch file is
        // still there because the best-effort delete was refused, and that is not a store failure.
        relativePath.Should().BeNull();
        ReportExists().Should().BeFalse("summary.md is created only by the move, which never ran");
        (await File.ReadAllBytesAsync(TempPath())).Should().Equal(leftOver);
        issues.Should().ContainSingle().Which.Code.Should().Be(CatalogReportWriter.WriteFailureCode);
    }

    [Fact]
    public async Task WriteAsync_WhenCancelled_StopsTheReportAndRecordsNoIssue()
    {
        // Arrange — the catalog on disk is finished; only the courtesy file is being cancelled (D4.2).
        await CreateCatalogAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var issues = new List<InterchangeIssue>();

        // Act
        var relativePath = await WriteAsync(issues, cts.Token);

        // Assert — nothing malfunctioned, so nothing is reported as one: no path, no issue, no file.
        relativePath.Should().BeNull();
        issues.Should().BeEmpty("a cancel is a stop, not a failure (D4.2)");
        ReportExists().Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_RejectsAMissingInput_InsteadOfWritingAnEmptyReport()
    {
        await CreateCatalogAsync();

        // Assert — the stage is called from the exporter's happy path only, so a null model is a bug in the
        // caller and must surface as one rather than as a catalog with a broken summary.
        var statistics = ReportModel.Statistics(Fixture());
        var map = ReportModel.MapOf(Fixture());
        await FluentActions.Invoking(() => _sut.WriteAsync(
                _catalogPath, statistics, map, null, null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => _sut.WriteAsync(
                "", statistics, map, null, [], CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        ReportExists().Should().BeFalse();
    }

    [Fact]
    public void TheReportInputsCarryNoFieldThatCouldHoldACredential()
    {
        // Arrange — FR9's structural half: the document is built from the two report models, and the name
        // of the catalog folder is the only string the writer receives. No target, no connection text.
        var writeParameters = typeof(CatalogReportWriter)
            .GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();
        var renderParameters = typeof(MarkdownCatalogReportRenderer)
            .GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        // Assert
        writeParameters.Should().Equal(
            typeof(string), typeof(CatalogStatistics), typeof(RelationMap), typeof(ScriptResult),
            typeof(IList<InterchangeIssue>), typeof(CancellationToken));
        renderParameters.Should().Equal(typeof(CatalogStatistics), typeof(RelationMap), typeof(ScriptResult));

        // Neither model can hold a secret: the words below are the kinds of member a credential would
        // arrive under, and the report models carry no such member, so no redaction logic exists to forget.
        // "port" is matched as a whole name only — it is the only one of these that a statistics property
        // collides with by accident, and the database name is deliberately not on the list, since FR2
        // requires the report to print it.
        string[] credentialWords = ["password", "secret", "token", "credential", "connection", "sslmode", "host", "user"];
        string[] connectionNames = ["port", "dsn", "connectionstring", "username", "password"];
        foreach (var type in renderParameters)
        {
            // The script summary is the one input that is not a report model; it is still the product's
            // own record of what it wrote, never a consumer's connection state.
            type.Namespace.Should().BeOneOf(
                ["DatabaseInterchange.Report", "DatabaseInterchange.Script"],
                "the document reads the product's own models, never a consumer's connection state");
            var suspicious = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(p => p.Name.ToLowerInvariant())
                .Where(name => credentialWords.Any(name.Contains) || connectionNames.Contains(name))
                .ToArray();
            suspicious.Should().BeEmpty($"{type.Name} must have no member a credential could be handed to");
        }
    }

    private async Task<string?> WriteAsync(IList<InterchangeIssue> issues, CancellationToken ct = default)
    {
        var snapshot = Fixture();

        return await _sut.WriteAsync(
            _catalogPath,
            ReportModel.Statistics(snapshot),
            ReportModel.MapOf(snapshot),
            null,
            issues,
            ct);
    }

    /// <summary>A two-table catalog with one relation, enough to make every section non-empty.</summary>
    private static InterchangeSnapshot Fixture() =>
        ReportModel.Snapshot(
            ReportModel.KeyedTable("public", "customers"),
            ReportModel.KeyedTable("public", "orders") with
            {
                ForeignKeys =
                [
                    ReportModel.Reference(
                        "public", "orders", "fk_orders_customer", "public", "customers",
                        ["customer_id"], ["id"]),
                ],
            });

    private async Task CreateCatalogAsync()
    {
        await using var writer = await CatalogWriter.CreateAsync(
            _catalogPath, producer: "DbClone-test", sourceDialect: "postgresql");
        await writer.WriteSectionAsync("schemas", [ReportModel.Schema("public")]);
        await writer.WriteSectionAsync("tables", [.. Fixture().Tables]);
        await writer.CompleteAsync();
    }

    private string ReportPath() => Path.Combine(_catalogPath, CatalogReportWriter.ReportFileName);

    private string TempPath() => Path.Combine(_catalogPath, CatalogReportWriter.TempFileName);

    private bool ReportExists() => File.Exists(ReportPath());

    private bool TempExists() => File.Exists(TempPath());

    private static FileStream OpenWithExclusiveLock(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>The UTF-8 bytes of a text with no BOM and no newline translation, as a file holds them.</summary>
    private static byte[] Utf8Bytes(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
}
