using DbAdapter.Definitions;

namespace DbAdapter.PostgreSql.Metadata;

/// <summary>
/// The pure scope-to-section decision: which of the twenty <see cref="EPgMetadataSection"/>s a
/// read under a given <see cref="EMetadataScope"/> executes. An unrecognized scope reads
/// everything — the safe default, matching the contract's permission-to-skip semantics.
/// </summary>
public static class PgMetadataSectionPlan
{
    /// <summary>
    /// Returns true when a read under <paramref name="scope"/> executes
    /// <paramref name="section"/>.
    /// </summary>
    public static bool Reads(EMetadataScope scope, EPgMetadataSection section) =>
        scope switch
        {
            EMetadataScope.Everything => true,
            EMetadataScope.Structure => section is
                EPgMetadataSection.ServerFacts
                or EPgMetadataSection.Extensions
                or EPgMetadataSection.Schemas
                or EPgMetadataSection.Tables
                or EPgMetadataSection.Columns
                or EPgMetadataSection.Indexes
                or EPgMetadataSection.ForeignKeys
                or EPgMetadataSection.Checks
                or EPgMetadataSection.UniqueKeys,
            _ => true,
        };
}
