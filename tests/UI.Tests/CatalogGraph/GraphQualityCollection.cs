namespace UI.Tests.CatalogGraph;

/// <summary>Timing characterization runs without competing test collections; ordinary graph tests remain parallel.</summary>
[CollectionDefinition("Graph quality characterization", DisableParallelization = true)]
public sealed class GraphQualityCollection;
