using System.Text;
using DatabaseInterchange.Format;
using FluentAssertions;
using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// Vectors for <see cref="CsvField"/> — the CSV encoding/decoding contract for v2 row files.
/// Each value is written and re-read byte-exact.
/// </summary>
public sealed class CsvFieldTests
{
    // Encode: basic values.

    [Fact]
    public void Encode_PlainText_NoQuoting()
    {
        CsvField.Encode("hello").Should().Be("hello");
    }

    [Fact]
    public void Encode_Integer_NoQuoting()
    {
        CsvField.Encode("42").Should().Be("42");
    }

    [Fact]
    public void Encode_Null_BareBackslashN()
    {
        CsvField.Encode(null).Should().Be(@"\N");
    }

    [Fact]
    public void Encode_EmptyString_Quoted()
    {
        CsvField.Encode("").Should().Be("\"\"");
    }

    // Encode: values that need quoting.

    [Fact]
    public void Encode_ContainsComma_Quoted()
    {
        CsvField.Encode("a,b").Should().Be("\"a,b\"");
    }

    [Fact]
    public void Encode_ContainsDoubleQuote_QuotedAndDoubled()
    {
        CsvField.Encode("a\"b").Should().Be("\"a\"\"b\"");
    }

    [Fact]
    public void Encode_ContainsCR_Quoted()
    {
        CsvField.Encode("a\rb").Should().Be("\"a\rb\"");
    }

    [Fact]
    public void Encode_ContainsLF_Quoted()
    {
        CsvField.Encode("a\nb").Should().Be("\"a\nb\"");
    }

    [Fact]
    public void Encode_ContainsCRLF_Quoted()
    {
        CsvField.Encode("a\r\nb").Should().Be("\"a\r\nb\"");
    }

    [Fact]
    public void Encode_LeadingSpace_Quoted()
    {
        CsvField.Encode(" hello").Should().Be("\" hello\"");
    }

    [Fact]
    public void Encode_TrailingSpace_Quoted()
    {
        CsvField.Encode("hello ").Should().Be("\"hello \"");
    }

    [Fact]
    public void Encode_LeadingTab_Quoted()
    {
        CsvField.Encode("\thello").Should().Be("\"\thello\"");
    }

    [Fact]
    public void Encode_TrailingTab_Quoted()
    {
        CsvField.Encode("hello\t").Should().Be("\"hello\t\"");
    }

    [Fact]
    public void Encode_LiteralBackslashN_Quoted()
    {
        // A value that is literally the two characters \N must be quoted to distinguish from NULL.
        CsvField.Encode(@"\N").Should().Be(@"""\N""");
    }

    // Decode: round-trip.

    [Fact]
    public void Decode_PlainText_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("hello")).Should().Be("hello");
    }

    [Fact]
    public void Decode_Null_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode(null)).Should().BeNull();
    }

    [Fact]
    public void Decode_EmptyString_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("")).Should().Be("");
    }

    [Fact]
    public void Decode_Comma_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("a,b")).Should().Be("a,b");
    }

    [Fact]
    public void Decode_DoubleQuote_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("a\"b")).Should().Be("a\"b");
    }

    [Fact]
    public void Decode_Newline_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("a\nb")).Should().Be("a\nb");
    }

    [Fact]
    public void Decode_CR_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("a\rb")).Should().Be("a\rb");
    }

    [Fact]
    public void Decode_CRLF_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("a\r\nb")).Should().Be("a\r\nb");
    }

    [Fact]
    public void Decode_LeadingSpace_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode(" hello")).Should().Be(" hello");
    }

    [Fact]
    public void Decode_TrailingSpace_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode("hello ")).Should().Be("hello ");
    }

    [Fact]
    public void Decode_LiteralBackslashN_RoundTrips()
    {
        CsvField.Decode(CsvField.Encode(@"\N")).Should().Be(@"\N");
    }

    // Decode: edge cases.

    [Fact]
    public void Decode_BareBackslashN_IsNull()
    {
        CsvField.Decode(@"\N").Should().BeNull();
    }

    [Fact]
    public void Decode_QuotedBackslashN_IsData()
    {
        CsvField.Decode(@"""\N""").Should().Be(@"\N");
    }

    [Fact]
    public void Decode_QuotedEmpty_IsEmptyString()
    {
        CsvField.Decode("\"\"").Should().Be("");
    }

    // Unicode and large values.

    [Fact]
    public void Encode_ControlCharacter_QuotesAndRoundTrips()
    {
        var value = "before\u0001after";
        var encoded = CsvField.Encode(value);

        encoded.Should().Be("\"before\u0001after\"");
        CsvField.Decode(encoded).Should().Be(value);
    }

    [Fact]
    public void Encode_Emoji_NoQuoting()
    {
        CsvField.Encode("😀").Should().Be("😀");
    }

    [Fact]
    public void Encode_CJK_NoQuoting()
    {
        CsvField.Encode("日本語").Should().Be("日本語");
    }

    [Fact]
    public void Encode_LargeJsonToken_NoQuotingUnlessNeeded()
    {
        var json = "{\"key\": \"value\"}";
        // Contains quotes and colon-space, needs quoting for the quotes.
        var encoded = CsvField.Encode(json);
        encoded.Should().StartWith("\"");
        CsvField.Decode(encoded).Should().Be(json);
    }

    [Fact]
    public void Encode_Base64Bytea_NoQuoting()
    {
        var base64 = "dGVzdA==";
        CsvField.Encode(base64).Should().Be(base64);
    }

    [Fact]
    public void RoundTrip_3000DoubleQuotes()
    {
        var value = new string('"', 3000);
        var encoded = CsvField.Encode(value);
        CsvField.Decode(encoded).Should().Be(value);
    }

    [Fact]
    public void RoundTrip_100KJsonToken()
    {
        var value = "{\"data\":\"" + new string('x', 100_000) + "\"}";
        var encoded = CsvField.Encode(value);
        CsvField.Decode(encoded).Should().Be(value);
    }

    // WriteRecord / ParseRecord.

    [Fact]
    public void WriteRecord_SimpleValues()
    {
        var sb = new StringBuilder();
        CsvField.WriteRecord(sb, ["hello", "42", null, ""]);
        sb.ToString().Should().Be("hello,42,\\N,\"\"\n");
    }

    [Fact]
    public void ParseRecord_SimpleValues()
    {
        var (fields, consumed) = CsvField.ParseRecord("hello,42,\\N,\"\"\n");
        fields.Should().Equal("hello", "42", null, "");
        consumed.Should().Be("hello,42,\\N,\"\"\n".Length);
    }

    [Fact]
    public void ParseRecord_QuotedFieldWithComma()
    {
        var (fields, _) = CsvField.ParseRecord("\"a,b\",c\n");
        fields.Should().Equal("a,b", "c");
    }

    [Fact]
    public void ParseRecord_QuotedFieldWithNewline()
    {
        var (fields, consumed) = CsvField.ParseRecord("\"a\nb\",c\n");
        fields.Should().Equal("a\nb", "c");
        consumed.Should().Be("\"a\nb\",c\n".Length);
    }

    [Fact]
    public void ParseRecord_QuotedFieldWithDoubleQuote()
    {
        var (fields, _) = CsvField.ParseRecord("\"a\"\"b\",c\n");
        fields.Should().Equal("a\"b", "c");
    }

    [Fact]
    public void WriteRecord_RoundTripsThroughParseRecord()
    {
        var values = new string?[] { "hello", "42", null, "", "a,b", "a\"b", "a\nb", @"\N", "😀", "日本語" };
        var sb = new StringBuilder();
        CsvField.WriteRecord(sb, values);

        var (fields, _) = CsvField.ParseRecord(sb.ToString());
        fields.Should().Equal(values);
    }

    // Backslash is literal in CSV (no escape language).

    [Fact]
    public void Encode_BackslashX_NoQuoting()
    {
        // \d, \x etc. are ordinary data — no backslash escape language.
        CsvField.Encode(@"\d").Should().Be(@"\d");
    }

    [Fact]
    public void Encode_WindowsPath_NoQuoting()
    {
        CsvField.Encode(@"C:\Users\x").Should().Be(@"C:\Users\x");
    }

    [Fact]
    public void Decode_BackslashX_IsLiteral()
    {
        CsvField.Decode(@"\d").Should().Be(@"\d");
    }

    [Fact]
    public void Decode_WindowsPath_IsLiteral()
    {
        CsvField.Decode(@"C:\Users\x").Should().Be(@"C:\Users\x");
    }
}
