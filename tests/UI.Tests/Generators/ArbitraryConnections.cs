using DbClone.UI.Models;

using FsCheck;
using FsCheck.Fluent;

using Gen = FsCheck.Fluent.Gen;
using Arb = FsCheck.Fluent.Arb;

namespace UI.Tests.Generators;

/// <summary>
/// Custom FsCheck generators for <see cref="SavedConnection"/> and <see cref="ConnectionGroup"/>
/// producing realistic but random data for property-based tests.
/// </summary>
public static class ArbitraryConnections
{
    private static readonly char[] AlphanumericChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private static readonly char[] HexChars = "0123456789ABCDEF".ToCharArray();

    private static readonly string[] ValidSslModes = ["Disable", "Prefer", "Require"];

    /// <summary>
    /// Custom Arbitrary for <see cref="ConnectionGroup"/>.
    /// Produces random Name (1-20 chars), valid-format GUID references for
    /// SourceConnectionId and DestinationConnectionId, Notes (0-100 chars), and optional Color.
    /// </summary>
    public static Arbitrary<ConnectionGroup> ConnectionGroupArbitrary() =>
        Arb.From(
            from name in AlphanumericString(1, 20)
            from sourceId in Gen.Fresh(() => Guid.NewGuid().ToString("N"))
            from destId in Gen.Fresh(() => Guid.NewGuid().ToString("N"))
            from notes in AlphanumericString(0, 100)
            from color in OptionalHexColor()
            select new ConnectionGroup
                       {
                           Id = Guid.NewGuid().ToString("N"),
                           Name = name,
                           SourceConnectionId = sourceId,
                           DestinationConnectionId = destId,
                           Notes = notes,
                           Color = color
                       });

    /// <summary>
    /// Custom Arbitrary for <see cref="SavedConnection"/>.
    /// Produces random Name (1-20 chars), Host (1-50 chars), Port (1-65535),
    /// DatabaseName (1-20 chars), Username (1-20 chars), Password (0-30 chars),
    /// valid SslMode, valid ConnectionType, Notes (0-100 chars), and optional Color.
    /// </summary>
    public static Arbitrary<SavedConnection> SavedConnectionArbitrary() =>
        Arb.From(
            from name in AlphanumericString(1, 20)
            from host in AlphanumericString(1, 50)
            from port in Gen.Choose(1, 65535)
            from dbName in AlphanumericString(1, 20)
            from username in AlphanumericString(1, 20)
            from password in AlphanumericString(0, 30)
            from sslMode in Gen.Elements(ValidSslModes)
            from connectionType in Gen.Elements("postgresql", "supabase", "neon", "aiven", "azure")
            from notes in AlphanumericString(0, 100)
            from color in OptionalHexColor()
            select new SavedConnection
                       {
                           Id = Guid.NewGuid().ToString("N"),
                           Name = name,
                           Host = host,
                           Port = port.ToString(),
                           DatabaseName = dbName,
                           Username = username,
                           Password = password,
                           SslMode = sslMode,
                           ConnectionType = connectionType,
                           Notes = notes,
                           Color = color
                       });

    /// <summary>
    /// Generates a random alphanumeric string of the specified length range.
    /// </summary>
    private static Gen<string> AlphanumericString(int minLength, int maxLength) =>
        from length in Gen.Choose(minLength, maxLength)
        from chars in Gen.Elements(AlphanumericChars).ArrayOf(length)
        select new string(chars);

    /// <summary>
    /// Generates a random hex color string like "#4CAF50" or null.
    /// </summary>
    private static Gen<string?> OptionalHexColor() =>
        Gen.OneOf(
            Gen.Constant<string?>(null),
            from chars in Gen.Elements(HexChars).ArrayOf(6)
            select (string?)("#" + new string(chars)));
}
