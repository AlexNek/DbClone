namespace DatabaseInterchange.Script;

/// <summary>
/// The shape a data script family was chosen for, measured from the catalog before a byte is written (D2).
/// </summary>
/// <remarks>
/// The names are the plan's own: <c>S1</c> fits, <c>S2</c> many small tables, <c>S3</c> giants, <c>S4</c> no
/// rows. Nothing chooses between them — the catalog's size distribution does — which is why the report states
/// the profile rather than offering it.
/// </remarks>
public enum EScriptProfile
{
    /// <summary>The whole catalog fits one file, or the budget is zero: today's single <c>data.sql</c>.</summary>
    Fits,

    /// <summary>Every table fits the budget and the catalog does not: numbered files, table blocks packed.</summary>
    ManySmall,

    /// <summary>At least one table is larger than the budget: that table's block is split into parts.</summary>
    Giants,

    /// <summary>The catalog holds no rows, so there is no data family at all: the tail stays in <c>schema.sql</c>.</summary>
    NoRows,
}
