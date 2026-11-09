using DbClone.UI.Models;
using DbClone.UI.ViewModels;

using Wpf.Ui.Controls;

namespace DbClone.UI.StoreRestore.Validators;

/// <summary>
/// Validates workflow prerequisites.
/// Single responsibility: connection and path validation.
/// </summary>
public interface IWorkflowValidator
{
    bool ValidateSourceConnection(ConnectionViewModel source, WorkflowState state);
    bool ValidateDestinationConnection(ConnectionViewModel destination, WorkflowState state);
    bool ValidateDirectoryExists(string path, WorkflowState state);
    bool EnsureDirectoryExists(string path, WorkflowState state);
}

/// <summary>
/// Default implementation of <see cref="IWorkflowValidator"/>.
/// </summary>
public sealed class WorkflowValidator : IWorkflowValidator
{
    public bool ValidateSourceConnection(ConnectionViewModel source, WorkflowState state)
    {
        if (!string.IsNullOrWhiteSpace(source.DatabaseName)) return true;

        state.BeginNewRun();
        state.LogError("Source connection has no database name. Store requires a specific database.");
        state.ShowBanner("Store blocked", "The source connection has no database name.", InfoBarSeverity.Error);
        return false;
    }

    public bool ValidateDestinationConnection(ConnectionViewModel destination, WorkflowState state)
    {
        if (!string.IsNullOrWhiteSpace(destination.DatabaseName)) return true;

        state.BeginNewRun();
        state.LogError("Destination connection has no database name. Restore requires a specific database.");
        state.ShowBanner("Restore blocked", "The destination connection has no database name.", InfoBarSeverity.Error);
        return false;
    }

    public bool ValidateDirectoryExists(string path, WorkflowState state)
    {
        if (System.IO.Directory.Exists(path)) return true;

        state.LogError($"Selected directory does not exist: {path}");
        return false;
    }

    public bool EnsureDirectoryExists(string path, WorkflowState state)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }
            return true;
        }
        catch (System.Exception ex)
        {
            state.LogError($"Failed to create directory: {ex.Message}");
            return false;
        }
    }
}
