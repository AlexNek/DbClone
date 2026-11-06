using DbAdapter.Definitions;
using CanonicalModel.DTOs;

namespace DbAdapter.PostgreSql.Adapters;

/// <summary>
/// The copy role, forwarding to the engine that runs the pipeline.
/// </summary>
internal sealed class CopySessionAdapter : ICopySession
{
    private readonly ICopyEngine _engine;

    public CopySessionAdapter(ICopyEngine engine) =>
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc />
    public Task<CopyResult> ExecuteAsync(
        CopyRequest request,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken) =>
        _engine.ExecuteCopyAsync(request, progress, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to release: the engine opens and closes both connections inside the call,
    /// so the session holds no resource between runs.
    /// </remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
