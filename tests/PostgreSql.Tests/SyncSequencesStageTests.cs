using DbClone.Application.Copy;
using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.Application.Interfaces;
using DbClone.Application.Models;
using DbClone.PostgreSql.Ddl;
using DbClone.PostgreSql.Execution;
using DbClone.PostgreSql.Pipeline;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PostgreSql.Tests;

/// <summary>
/// Hermetic tests for <see cref="SyncSequencesStage"/>: executors are mocked and
/// the connections in the context are constructed but never opened, so the suite
/// runs without a live database (e.g. on GitHub CI).
/// </summary>
public class SyncSequencesStageTests
{
    private const string LastValueSqlMarker = "SELECT last_value";
    private const string SerialResolveSqlMarker = "pg_get_serial_sequence";

    private readonly List<string> _destSql = [];
    private readonly ISqlExecutor _destExec = Substitute.For<ISqlExecutor>();
    private readonly IPgExecutorFactory _factory = Substitute.For<IPgExecutorFactory>();
    private readonly ISqlExecutor _sourceExec = Substitute.For<ISqlExecutor>();
    private readonly SyncSequencesStage _stage;

    public SyncSequencesStageTests()
    {
        _stage = new SyncSequencesStage(new PgDdlGenerator(), _factory, NullLoggerFactory.Instance);

        // Route the executors by connection instance, like the stage resolves them
        _factory
            .Create(Arg.Any<NpgsqlConnection>())
            .Returns(
                ci => ReferenceEquals(ci.Arg<NpgsqlConnection>(), SourceConnection)
                    ? _sourceExec
                    : _destExec);

        // Capture every statement the stage sends to the destination
        _destExec
            .ExecuteNonQueryAsync(Arg.Do<string>(sql => _destSql.Add(sql)), Arg.Any<CancellationToken>())
            .Returns(0);
    }

    private static NpgsqlConnection SourceConnection { get; } =
        new("Host=test.example.com;Database=srcdb;Username=test_user;Password=fake_pw_123");

    private static NpgsqlConnection DestinationConnection { get; } =
        new("Host=test.example.com;Database=dstdb;Username=test_user;Password=fake_pw_123");

    [Theory]
    [InlineData(ECopyMode.Resume)]
    [InlineData(ECopyMode.Update)]
    public async Task ExecuteAsync_ResumeOrUpdateMode_SkipsWithoutTouchingDatabase(ECopyMode mode)
    {
        var context = CreateContext(mode, [MakeSequence("public", "some_seq")]);

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        result.Details.Should().ContainSingle(d => d.Kind == EStageMessageKind.Skipped);
        _factory.DidNotReceive().Create(Arg.Any<NpgsqlConnection>());
    }

    [Fact]
    public async Task ExecuteAsync_StandaloneSequence_SetsValueOnDestination()
    {
        var context = CreateContext(ECopyMode.Full, [MakeSequence("public", "standalone_sel_seq")]);
        StubSourceSequenceValue(1234);

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        _destSql.Should().ContainSingle().Which.Should().Be(
            "SELECT setval('public.standalone_sel_seq', 1234, true);");
        context.Statistics.SequencesSynced.Should().Be(1);
        context.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseStandaloneSequence_QuotesNameInSetval()
    {
        // Unquoted setval arguments are case-folded to lowercase by PostgreSQL,
        // so mixed-case names must arrive quoted inside the string literal
        var context = CreateContext(ECopyMode.Full, [MakeSequence("sel_test", "Mixed_Seq")]);
        StubSourceSequenceValue(42);

        await _stage.ExecuteAsync(context);

        _destSql.Should()
                .ContainSingle()
                .Which.Should()
                .Be("SELECT setval('sel_test.\"Mixed_Seq\"', 42, true);");
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseSerialSequence_PassesQuotedTableAndRawColumnToResolver()
    {
        // pg_get_serial_sequence parses the table argument as an identifier (must be
        // quoted to survive case-folding) but matches the column argument LITERALLY
        // against pg_attribute (must stay raw — quoting would search for a column
        // whose name contains quote characters)
        var sequence = MakeSequence(
            "sel_test",
            "MixedCase_Id_seq",
            ownerTable: "sel_test.MixedCase",
            ownerColumn: "Id");
        var context = CreateContext(ECopyMode.Full, [sequence]);
        StubSourceSequenceValue(77);

        string? resolveSql = null;
        _destExec
            .ExecuteScalarAsync<string>(
                Arg.Do<string>(sql => resolveSql = sql),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Scalar query returned null"));

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        resolveSql!.Should().Contain("pg_get_serial_sequence('sel_test.\"MixedCase\"', 'Id')");
    }

    [Fact]
    public async Task ExecuteAsync_SerialSequenceNotResolvable_FallsBackToSourceNameAndRepairsOwnership()
    {
        var sequence = MakeSequence(
            "sel_test",
            "MixedCase_Id_seq",
            ownerTable: "sel_test.MixedCase",
            ownerColumn: "Id");
        var context = CreateContext(ECopyMode.Full, [sequence]);
        StubSourceSequenceValue(77);
        StubSerialSequenceResolution(resolved: null);

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        _destSql.Should().HaveCount(2);
        _destSql[0].Should().Be(
            "ALTER SEQUENCE sel_test.\"MixedCase_Id_seq\" OWNED BY sel_test.\"MixedCase\".\"Id\"");
        _destSql[1].Should().Be("SELECT setval('sel_test.\"MixedCase_Id_seq\"', 77, true);");
        context.Statistics.SequencesSynced.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_SerialSequenceResolvedOnDestination_UsesResolvedName()
    {
        var sequence = MakeSequence(
            "public",
            "users_id_seq",
            ownerTable: "public.users",
            ownerColumn: "id");
        var context = CreateContext(ECopyMode.Full, [sequence]);
        StubSourceSequenceValue(500);
        StubSerialSequenceResolution(resolved: "public.\"OddSeq\"");

        await _stage.ExecuteAsync(context);

        // The resolved name comes back quoted from pg_get_serial_sequence and must
        // survive re-quoting for setval without corruption
        _destSql.Should().ContainSingle().Which.Should().Be(
            "SELECT setval('public.\"OddSeq\"', 500, true);");
    }

    [Fact]
    public async Task ExecuteAsync_IdentitySequenceWithoutDestinationSequence_SkipsWithWarning()
    {
        var sequence = MakeSequence(
            "public",
            "probe_id_from_owned_seq",
            ownerTable: "public.seq_probe",
            ownerColumn: "id_from_owned",
            isIdentity: true);
        var context = CreateContext(ECopyMode.Full, [sequence]);
        StubSourceSequenceValue(10);
        StubSerialSequenceResolution(resolved: null);

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        _destSql.Should().BeEmpty();
        context.Statistics.SequencesSynced.Should().Be(0);
        result.Details.Should().Contain(
            d => d.Kind == EStageMessageKind.Skipped && d.Level == ELogLevel.Warning);
        context.Warnings.Should().ContainSingle().Which.Kind.Should().Be(EStageMessageKind.Skipped);
    }

    [Fact]
    public async Task ExecuteAsync_SetvalFails_ReportsWarningButStageSucceeds()
    {
        var context = CreateContext(ECopyMode.Full, [MakeSequence("public", "broken_seq")]);
        StubSourceSequenceValue(5);
        _destExec
            .ExecuteNonQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("relation does not exist"));

        var result = await _stage.ExecuteAsync(context);

        result.Success.Should().BeTrue();
        context.Statistics.SequencesSynced.Should().Be(0);
        result.Details.Should().Contain(
            d => d.Kind == EStageMessageKind.Failed && d.Level == ELogLevel.Warning);
        context.Warnings.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                StageName = ECopyStage.SyncSequences,
                Kind = EStageMessageKind.Failed,
                ObjectName = "public.broken_seq"
            },
            opts => opts.ExcludingMissingMembers());
    }

    private static CopyContext CreateContext(ECopyMode mode, IReadOnlyList<SequenceDefinition> sequences)
    {
        var connection = new ConnectionInfo(
            "test.example.com",
            5432,
            "testdb",
            "test_user",
            "fake_pw_123",
            ESslMode.Disable);

        return new CopyContext
                   {
                       Request = new CopyRequest(connection, connection, new CopyOptions(CopyMode: mode)),
                       SourceModel = new DatabaseModel(
                           "testdb",
                           "PostgreSQL 16.0",
                           [],
                           [],
                           [],
                           [],
                           sequences,
                           [],
                           [],
                           [],
                           [],
                           [],
                           [],
                           [],
                           [],
                           []),
                       SourceConnection = SourceConnection,
                       DestinationConnection = DestinationConnection
                   };
    }

    private static SequenceDefinition MakeSequence(
        string schema,
        string name,
        string? ownerTable = null,
        string? ownerColumn = null,
        bool isIdentity = false) =>
        new(schema, name, 1, 1, null, null, 1, false, "bigint", null, ownerTable, ownerColumn, isIdentity);

    private void StubSourceSequenceValue(long value) =>
        _sourceExec
            .ExecuteScalarAsync<long>(
                Arg.Is<string>(sql => sql != null && sql.StartsWith(LastValueSqlMarker, StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .Returns(value);

    private void StubSerialSequenceResolution(string? resolved)
    {
        var call = _destExec.ExecuteScalarAsync<string>(
            Arg.Is<string>(sql => sql != null && sql.Contains(SerialResolveSqlMarker, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        if (resolved is null)
        {
            // Mirrors PgSqlExecutor behavior when pg_get_serial_sequence returns NULL
            call.ThrowsAsync(new InvalidOperationException("Scalar query returned null"));
        }
        else
        {
            call.Returns(resolved);
        }
    }
}
