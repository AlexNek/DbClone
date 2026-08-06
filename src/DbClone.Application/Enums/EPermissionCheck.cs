namespace DbClone.Application.Enums;

/// <summary>
/// Flags indicating which permission checks to perform.
/// </summary>
[Flags]
public enum EPermissionCheck
{
    None = 0,

    Connect = 1,

    CreateDatabase = 2,

    DropObjects = 4,

    CreateObjects = 8,

    InsertData = 16,

    All = Connect | CreateDatabase | DropObjects | CreateObjects | InsertData
}
