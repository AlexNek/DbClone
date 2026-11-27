namespace DbClone.UI.Services.GraphLayout;

/// <summary>The two cards one relation joins; the validator allows its route to touch only these.</summary>
/// <param name="ChildNode">The card the route starts on.</param>
/// <param name="ParentNode">The card the route ends on.</param>
internal sealed record GraphEdgeEndpoints(int ChildNode, int ParentNode);
