using CanonicalModel.DTOs;

namespace DbAdapter.Definitions;

/// <summary>
/// The copy role: reproduce the given model and its data in the request's target.
/// Owns the pipeline, its stages, its connections and their cleanup.
/// </summary>
public interface ICopySession : IAsyncDisposable
{
    Task<CopyResult> ExecuteAsync(
        CopyRequest request,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken);
}
