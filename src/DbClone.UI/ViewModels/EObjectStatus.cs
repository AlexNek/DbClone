namespace DbClone.UI.ViewModels;

/// <summary>Processing status of an object type.</summary>
public enum EObjectStatus
{
    /// <summary>Not yet started.</summary>
    Pending,

    /// <summary>Currently being processed.</summary>
    InProgress,

    /// <summary>Processing complete.</summary>
    Done,

    /// <summary>Processing finished with errors.</summary>
    Failed
}
