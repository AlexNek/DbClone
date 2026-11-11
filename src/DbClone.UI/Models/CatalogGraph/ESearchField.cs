namespace DbClone.UI.Models.CatalogGraph;

/// <summary>The field of a node that a search query matched.</summary>
internal enum ESearchField
{
    /// <summary>The schema name.</summary>
    Schema,

    /// <summary>The table name.</summary>
    Table,

    /// <summary>A column name.</summary>
    Column,

    /// <summary>A constraint name the table declares.</summary>
    Constraint,

    /// <summary>A captured column comment.</summary>
    Comment,
}
