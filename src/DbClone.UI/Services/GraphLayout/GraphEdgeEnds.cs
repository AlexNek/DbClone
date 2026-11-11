namespace DbClone.UI.Services.GraphLayout;

/// <summary>
/// The two ends one constraint owns: the end on the table that stores the value and the end on the
/// table the value comes from.
/// </summary>
/// <param name="Child">The end that sits on the child's card.</param>
/// <param name="Parent">The end that sits on the parent's card.</param>
internal sealed record GraphEdgeEnds(GraphEdgePort Child, GraphEdgePort Parent);
