namespace DbClone.Application.Relationship;

/// <summary>
/// Which side of the window a relationship read was asked for. It names the request in the log and in
/// the handler's messages; the document itself never prints it, because one origin is one picture.
/// </summary>
public enum ERelationshipRole
{
    /// <summary>No side: a stored catalog the user picked from disk.</summary>
    None,

    /// <summary>The source connection panel.</summary>
    Source,

    /// <summary>The destination connection panel.</summary>
    Target
}
