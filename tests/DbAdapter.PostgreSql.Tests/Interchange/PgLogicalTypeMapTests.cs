using CanonicalModel.Interchange;

using DbAdapter.PostgreSql.Interchange;

using FluentAssertions;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// The adapter-owned type table. The format forbids guessing, so the important half of this is
/// not the mapped names but the ones that must stay <see cref="ELogicalType.Unknown"/>.
/// </summary>
public class PgLogicalTypeMapTests
{
    [Theory]
    [InlineData("boolean", ELogicalType.Boolean)]
    [InlineData("int2", ELogicalType.Integer16)]
    [InlineData("smallserial", ELogicalType.Integer16)]
    [InlineData("integer", ELogicalType.Integer32)]
    [InlineData("serial4", ELogicalType.Integer32)]
    [InlineData("bigint", ELogicalType.Integer64)]
    [InlineData("numeric", ELogicalType.Decimal)]
    [InlineData("money", ELogicalType.Decimal)]
    [InlineData("real", ELogicalType.Float32)]
    [InlineData("double precision", ELogicalType.Float64)]
    [InlineData("text", ELogicalType.String)]
    [InlineData("citext", ELogicalType.String)]
    [InlineData("bpchar", ELogicalType.String)]
    [InlineData("bytea", ELogicalType.Binary)]
    [InlineData("date", ELogicalType.Date)]
    [InlineData("time without time zone", ELogicalType.Time)]
    [InlineData("timetz", ELogicalType.TimeWithTimeZone)]
    [InlineData("timestamp without time zone", ELogicalType.Timestamp)]
    [InlineData("timestamptz", ELogicalType.TimestampWithTimeZone)]
    [InlineData("interval", ELogicalType.Interval)]
    [InlineData("uuid", ELogicalType.Uuid)]
    [InlineData("jsonb", ELogicalType.Json)]
    [InlineData("xml", ELogicalType.Xml)]
    [InlineData("cidr", ELogicalType.Network)]
    [InlineData("macaddr8", ELogicalType.Network)]
    [InlineData("bit varying", ELogicalType.BitString)]
    [InlineData("polygon", ELogicalType.Geometry)]
    [InlineData("geometry", ELogicalType.Geometry)]
    [InlineData("vector", ELogicalType.Vector)]
    public void Classify_KnownTypeName_ReturnsItsLogicalType(string nativeType, ELogicalType expected) =>
        PgLogicalTypeMap.Classify(nativeType).Should().Be(expected);

    [Theory]
    [InlineData("integer[]")]
    [InlineData("text[][]")]
    [InlineData("numeric(10,2)[]")]
    public void Classify_ArrayType_ReturnsArrayBeforeItsElementType(string nativeType) =>
        PgLogicalTypeMap.Classify(nativeType).Should().Be(ELogicalType.Array);

    [Theory]
    [InlineData("int4range")]
    [InlineData("tsrange")]
    [InlineData("numrange")]
    public void Classify_RangeType_ReturnsRangeNotItsSubtype(string nativeType) =>
        PgLogicalTypeMap.Classify(nativeType).Should().Be(ELogicalType.Range);

    [Fact]
    public void Classify_AcceptsSchemaPrefixAndWhitespaceAndRefusesToGuessQuotedUserTypes()
    {
        // format_type() may report the schema-qualified name, and the metadata reader's text may
        // carry padding; both must still reach the same row of the table. A quoted name the table
        // does not define stays Unknown — the format forbids guessing a portable class.
        PgLogicalTypeMap.Classify("pg_catalog.integer").Should().Be(ELogicalType.Integer32);
        PgLogicalTypeMap.Classify("  integer  ").Should().Be(ELogicalType.Integer32);
        PgLogicalTypeMap.Classify("\"MySchema\".\"MyType\"").Should().Be(ELogicalType.Unknown);
    }

    [Theory]
    [InlineData("character varying(80)", ELogicalType.String)]
    [InlineData("varchar(80)", ELogicalType.String)]
    [InlineData("character(3)", ELogicalType.String)]
    [InlineData("numeric(10,2)", ELogicalType.Decimal)]
    [InlineData("decimal(12,3)", ELogicalType.Decimal)]
    [InlineData("bit(8)", ELogicalType.BitString)]
    [InlineData("bit varying(8)", ELogicalType.BitString)]
    [InlineData("interval(2)", ELogicalType.Interval)]
    [InlineData("timestamp(6) without time zone", ELogicalType.Timestamp)]
    [InlineData("timestamp(6) with time zone", ELogicalType.TimestampWithTimeZone)]
    [InlineData("time(4) without time zone", ELogicalType.Time)]
    [InlineData("time(4) with time zone", ELogicalType.TimeWithTimeZone)]
    [InlineData("vector(1536)", ELogicalType.Vector)]
    public void Classify_ModifierFoldedName_IsReadAsItsBaseType(string nativeType, ELogicalType expected) =>
        // format_type() writes the declared size into the name, so classification must survive the
        // parentheses — otherwise every bounded column would arrive as Unknown. The date/time types
        // put them before the zone phrase, so the two halves have to be rejoined rather than cut.
        PgLogicalTypeMap.Classify(nativeType).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("user_definedthing")]
    [InlineData("tsvector")]
    public void Classify_UnmappedName_StaysUnknown(string? nativeType) =>
        PgLogicalTypeMap.Classify(nativeType).Should().Be(ELogicalType.Unknown);

    [Fact]
    public void Classify_NameInEnumSet_IsEnumBothQualifiedAndBare()
    {
        var enums = new HashSet<string>(StringComparer.Ordinal) { "mood", "public.mood" };

        PgLogicalTypeMap.Classify("mood", enums).Should().Be(ELogicalType.Enum);
        PgLogicalTypeMap.Classify("public.mood", enums).Should().Be(ELogicalType.Enum);
    }

    [Fact]
    public void Classify_EnumSetTakesPrecedenceOverTheBuiltInTable() =>
        // A user type may be named like a range subtype; the database's own inventory wins.
        PgLogicalTypeMap
            .Classify("int4range", new HashSet<string>(StringComparer.Ordinal) { "int4range" })
            .Should()
            .Be(ELogicalType.Enum);

    [Theory]
    [InlineData("character varying(80)", 80, null, null)]
    [InlineData("varchar(80)", 80, null, null)]
    [InlineData("char(3)", 3, null, null)]
    [InlineData("bit(8)", 8, null, null)]
    [InlineData("vector(1536)", 1536, null, null)]
    [InlineData("numeric(10,2)", null, 10, 2)]
    [InlineData("decimal(12,3)", null, 12, 3)]
    [InlineData("integer", null, null, null)]
    [InlineData("timestamp without time zone", null, null, null)]
    [InlineData("text", null, null, null)]
    public void ReadModifiers_ReadsDeclaredSizesAndLeavesTheRestNull(
        string nativeType,
        int? expectedLength,
        int? expectedPrecision,
        int? expectedScale)
    {
        var (length, precision, scale) = PgLogicalTypeMap.ReadModifiers(nativeType);

        length.Should().Be(expectedLength);
        precision.Should().Be(expectedPrecision);
        scale.Should().Be(expectedScale);
    }

    [Fact]
    public void ReadModifiers_NumericWithoutScale_LeavesScaleNull()
    {
        var (length, precision, scale) = PgLogicalTypeMap.ReadModifiers("numeric(10)");

        length.Should().BeNull();
        precision.Should().Be(10);
        scale.Should().BeNull();
    }

    [Theory]
    [InlineData("timestamp with time zone", true)]
    [InlineData("timestamptz", true)]
    [InlineData("timestamp without time zone", false)]
    [InlineData("date", false)]
    public void IsUtcNormalized_OnlyForTheInstantType(string nativeType, bool expected) =>
        PgLogicalTypeMap.IsUtcNormalized(nativeType).Should().Be(expected);
}
