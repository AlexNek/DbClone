namespace DbClone.Application.Enums;

/// <summary>
/// Provides user-friendly display names for pipeline stages.
/// </summary>
public static class ECopyStageExtensions
{
    /// <summary>Returns a human-readable label for log output.</summary>
    public static string DisplayName(this ECopyStage stage) =>
        stage switch
            {
                ECopyStage.Connect => "Connect",
                ECopyStage.DetectCapabilities => "Detect Capabilities",
                ECopyStage.ReadMetadata => "Read Metadata",
                ECopyStage.ApplyTableFilter => "Apply Table Filter",
                ECopyStage.AnalyzeDependencies => "Analyze Dependencies",
                ECopyStage.Validate => "Validate",
                ECopyStage.CreateSchemas => "Schemas",
                ECopyStage.CreateExtensions => "Extensions",
                ECopyStage.CreateSequences => "Sequences",
                ECopyStage.CreateTypes => "Types",
                ECopyStage.CreateTables => "Tables",
                ECopyStage.ReconcileColumns => "Reconcile Columns",
                ECopyStage.CopyData => "Copy Data",
                ECopyStage.CreateIndexes => "Indexes",
                ECopyStage.CreateFunctions => "Functions",
                ECopyStage.RetryFunctions => "Functions (retry)",
                ECopyStage.CreateViews => "Views",
                ECopyStage.CreateTriggers => "Triggers",
                ECopyStage.CreateConstraints => "Constraints",
                ECopyStage.SyncSequences => "Sync Sequences",
                ECopyStage.ReCopyMismatched => "Re-copy Mismatched",
                ECopyStage.Complete => "Complete",
                ECopyStage.Failed => "Failed",
                ECopyStage.Orchestration => "Orchestration",
                _ => stage.ToString()
            };
}
