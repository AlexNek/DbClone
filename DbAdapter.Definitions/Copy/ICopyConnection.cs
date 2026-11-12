namespace DbAdapter.Definitions;

/// <summary>
/// Adapter-owned connection handle used by the provider-neutral copy pipeline.
/// The handle owns reopening and disposal, so a connection string cannot drift away
/// from the connection it recreates.
/// </summary>
public interface ICopyConnection : IAsyncDisposable
{
    /// <summary>Gets the connection identity used when reopening the handle.</summary>
    string ConnectionString { get; }

    /// <summary>Reopens the underlying provider connection when it is closed or broken.</summary>
    ValueTask ReopenAsync(CancellationToken cancellationToken = default);
}
