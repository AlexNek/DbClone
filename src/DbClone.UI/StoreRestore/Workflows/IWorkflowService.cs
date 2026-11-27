namespace DbClone.UI.StoreRestore.Workflows;

/// <summary>
/// Generic interface for workflow services that execute an operation with a typed request and result.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public interface IWorkflowService<in TRequest, TResult>
    where TRequest : notnull
{
    /// <summary>
    /// Executes the workflow.
    /// </summary>
    /// <param name="request">The workflow request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The workflow result.</returns>
    Task<TResult> ExecuteAsync(TRequest request, CancellationToken ct);
}
