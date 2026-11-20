namespace DbClone.UI.Services.GraphLayout;

/// <summary>Why a layout request produced no picture at all.</summary>
internal enum ELayoutFailureKind
{
    /// <summary>The caller asked to stop. No picture is drawn, however many components had finished.</summary>
    Cancelled,

    /// <summary>At least one component ran out of ways to place its tables and route its relations.</summary>
    NoValidLayout,

    /// <summary>
    /// The elapsed-time watchdog fired and even the cheapest complete pass — the seeds it already had, no
    /// refining — reached no valid arrangement. The limit is the answer only when that effort found nothing.
    /// </summary>
    LayoutTimeLimit,
}
