using DbClone.UI.StoreRestore.Workflows;
using DbClone.UI.ViewModels;

using Wpf.Ui.Controls;

namespace DbClone.UI.StoreRestore.Handlers;

/// <summary>
/// Handles workflow results and updates UI state (logs, banners, status).
/// Single responsibility: format and display results.
/// </summary>
public interface IWorkflowResultHandler
{
    void HandleStoreResult(WorkflowState state, StoreResult result);
    void HandleRestoreResult(WorkflowState state, RestoreResult result);
    void HandleError(WorkflowState state, string operation, string message);
    void HandleCancellation(WorkflowState state, string operation);
}

/// <summary>
/// Default implementation of <see cref="IWorkflowResultHandler"/>.
/// </summary>
public sealed class WorkflowResultHandler : IWorkflowResultHandler
{
    public void HandleStoreResult(WorkflowState state, StoreResult result)
    {
        state.Log(result.Message);
        state.StatusBarSummary = result.Success ? $"Store succeeded — {result.TablesExported} tables" : "Store FAILED";

        var severity = result.HasIssues ? InfoBarSeverity.Warning : (result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        state.ShowBanner(result.Success ? "Store Complete" : "Store Failed", result.Message, severity);

        if (!string.IsNullOrEmpty(result.CatalogPath))
            state.LogDetail($"Catalog: {result.CatalogPath}");
        if (!string.IsNullOrEmpty(result.SummaryPath))
            state.LogDetail($"Summary: {result.SummaryPath}");
        else if (result.SummaryRequested)
            state.LogWarning("Summary: not written");

        // One line per file the store wrote, in the order the names were handed over: a user who ran the
        // store to get a script should not have to open the folder to find out whether there is one.
        if (result.SqlScripts is { } scripts && scripts.SchemaPath is { } schemaScript)
        {
            state.LogDetail($"SQL schema: {schemaScript} ({scripts.SchemaStatements} statements, {scripts.SchemaBytes} bytes)");
            if (scripts.DataPath is { } dataScript)
                state.LogDetail($"SQL data: {dataScript} ({scripts.DataStatements} statements, {scripts.Rows} rows, {scripts.DataBytes} bytes)");
        }
        else if (result.SqlScriptsRequested)
        {
            state.LogWarning("SQL scripts: not written");
        }

        if (!result.VerificationPassed && result.Success)
            state.LogWarning("Catalog verification did not pass.");
    }

    public void HandleRestoreResult(WorkflowState state, RestoreResult result)
    {
        state.Log(result.Message);

        if (result.WasCancelled)
        {
            state.StatusBarSummary = "Restore cancelled";
            state.LogWarning($"Restore cancelled after {result.ObjectsApplied} objects.");
            if (!string.IsNullOrEmpty(result.LastCompletedChunk))
                state.LogHint($"Resume point: {result.LastCompletedChunk}");
            return;
        }

        if (result.WasRefused)
        {
            state.StatusBarSummary = "Restore refused";
            state.ShowBanner("Restore Refused", result.Message, InfoBarSeverity.Warning);
            state.LogError("=== RESTORE REFUSED (no changes applied) ===");
            return;
        }

        state.StatusBarSummary = result.Success ? $"Restore succeeded — {result.ObjectsApplied} objects" : "Restore FAILED";
        var severity = result.HasIssues ? InfoBarSeverity.Warning : (result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        state.ShowBanner(result.Success ? "Restore Complete" : "Restore Failed", result.Message, severity);

        if (result.RowCountMismatches > 0)
            state.LogWarning($"{result.RowCountMismatches} table(s) have a row-count mismatch.");
    }

    public void HandleError(WorkflowState state, string operation, string message)
    {
        state.StatusBarSummary = $"{operation} FAILED";
        state.LastError = message;
        state.LogError($"{operation} failed: {message}");
    }

    public void HandleCancellation(WorkflowState state, string operation)
    {
        state.Log($"{operation} cancelled by user.");
        state.StatusBarSummary = $"{operation} cancelled";
    }
}
