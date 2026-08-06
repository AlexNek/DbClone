namespace DbClone.Application.Enums;

/// <summary>
/// Identifies pipeline stages by name, eliminating magic strings.
/// </summary>
public enum ECopyStage
{
    Connect,

    DetectCapabilities,

    ReadMetadata,

    AnalyzeDependencies,

    Validate,

    CreateSchemas,

    CreateExtensions,

    CreateSequences,

    CreateTypes,

    CreateTables,

    ReconcileColumns,

    CopyData,

    CreateIndexes,

    CreateFunctions,

    RetryFunctions,

    CreateViews,

    CreateTriggers,

    CreateConstraints,

    SyncSequences,

    ReCopyMismatched,

    Complete,

    Failed,

    /// <summary>
    /// Used for errors that occur outside the pipeline (e.g. in the orchestrator).
    /// </summary>
    Orchestration
}
