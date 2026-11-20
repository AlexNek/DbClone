namespace DbClone.UI.Services.Relationship;

/// <summary>
/// What the viewer says about a request that has no graph yet: which origin it is about, what is being
/// done to it, and — when there is nothing to draw — why. It is the page the window opens with, so a
/// relationship view states that it was asked for from the first moment instead of after the read.
/// </summary>
/// <param name="Origin">The origin the request named, as the window's title states it.</param>
/// <param name="Headline">The one sentence in the page: what is happening, or what could not happen.</param>
/// <param name="Detail">What the reader should know beside it: when the picture will come, or the reason it will not.</param>
/// <param name="IsFailure">Whether the notice states a failure rather than work in progress.</param>
internal sealed record RelationshipNotice(string Origin, string Headline, string Detail, bool IsFailure = false);
