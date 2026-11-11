namespace DbClone.UI.Models.CatalogGraph;

/// <summary>The three groups a cluster chip can belong to.</summary>
internal enum EClusterChipKind
{
    /// <summary>One chip per schema the catalog holds.</summary>
    Schema,

    /// <summary>One chip per cyclic group the analyzer found.</summary>
    CyclicGroup,

    /// <summary>One chip for the tables with no relation on either side.</summary>
    Unconnected,
}
