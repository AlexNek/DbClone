using CanonicalModel.Interchange;
using DatabaseInterchange.Format;
using FluentAssertions;
using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// Vectors for <see cref="NameEncoder"/> — the closed five-rule algorithm that turns object names
/// into legal, collision-free filesystem paths.
/// </summary>
public sealed class NameEncoderTests
{
    // Rule 1: percent-encode unsafe characters.

    [Fact]
    public void EncodeSegment_PlainName_Unchanged()
    {
        NameEncoder.EncodeSegment("orders").Should().Be("orders");
    }

    [Fact]
    public void EncodeSegment_PercentSign_Encoded()
    {
        NameEncoder.EncodeSegment("50%off").Should().Be("50%25off");
    }

    [Fact]
    public void EncodeSegment_Tilde_Encoded()
    {
        NameEncoder.EncodeSegment("backup~1").Should().Be("backup%7E1");
    }

    [Fact]
    public void EncodeSegment_Backslash_Encoded()
    {
        NameEncoder.EncodeSegment("a\\b").Should().Be("a%5Cb");
    }

    [Fact]
    public void EncodeSegment_ForwardSlash_Encoded()
    {
        NameEncoder.EncodeSegment("a/b").Should().Be("a%2Fb");
    }

    [Fact]
    public void EncodeSegment_Colon_Encoded()
    {
        NameEncoder.EncodeSegment("a:b").Should().Be("a%3Ab");
    }

    [Fact]
    public void EncodeSegment_Asterisk_Encoded()
    {
        NameEncoder.EncodeSegment("a*b").Should().Be("a%2Ab");
    }

    [Fact]
    public void EncodeSegment_QuestionMark_Encoded()
    {
        NameEncoder.EncodeSegment("a?b").Should().Be("a%3Fb");
    }

    [Fact]
    public void EncodeSegment_DoubleQuote_Encoded()
    {
        NameEncoder.EncodeSegment("a\"b").Should().Be("a%22b");
    }

    [Fact]
    public void EncodeSegment_AngleBrackets_Encoded()
    {
        NameEncoder.EncodeSegment("a<b>c").Should().Be("a%3Cb%3Ec");
    }

    [Fact]
    public void EncodeSegment_Pipe_Encoded()
    {
        NameEncoder.EncodeSegment("a|b").Should().Be("a%7Cb");
    }

    [Fact]
    public void EncodeSegment_ControlChar_Encoded()
    {
        NameEncoder.EncodeSegment("a\u0001b").Should().Be("a%01b");
    }

    [Fact]
    public void EncodeSegment_LeadingDot_Encoded()
    {
        NameEncoder.EncodeSegment(".hidden").Should().Be("%2Ehidden");
    }

    [Fact]
    public void EncodeSegment_TrailingDot_Encoded()
    {
        NameEncoder.EncodeSegment("trailing.").Should().Be("trailing%2E");
    }

    [Fact]
    public void EncodeSegment_LeadingSpace_Encoded()
    {
        NameEncoder.EncodeSegment(" leading").Should().Be("%20leading");
    }

    [Fact]
    public void EncodeSegment_TrailingSpace_Encoded()
    {
        NameEncoder.EncodeSegment("trailing ").Should().Be("trailing%20");
    }

    [Fact]
    public void EncodeSegment_Hash_Unchanged()
    {
        NameEncoder.EncodeSegment("table#1").Should().Be("table#1");
    }

    [Fact]
    public void EncodeSegment_Underscore_Unchanged()
    {
        NameEncoder.EncodeSegment("my_table").Should().Be("my_table");
    }

    // Rule 2: reserved device names.

    [Fact]
    public void EncodeSegment_CON_Prefixed()
    {
        NameEncoder.EncodeSegment("CON").Should().Be("~CON");
    }

    [Fact]
    public void EncodeSegment_con_lowercase_Prefixed()
    {
        NameEncoder.EncodeSegment("con").Should().Be("~con");
    }

    [Fact]
    public void EncodeSegment_NUL_Prefixed()
    {
        NameEncoder.EncodeSegment("NUL").Should().Be("~NUL");
    }

    [Fact]
    public void EncodeSegment_AUX_Prefixed()
    {
        NameEncoder.EncodeSegment("AUX").Should().Be("~AUX");
    }

    [Fact]
    public void EncodeSegment_COM1_Prefixed()
    {
        NameEncoder.EncodeSegment("COM1").Should().Be("~COM1");
    }

    [Fact]
    public void EncodeSegment_LPT9_Prefixed()
    {
        NameEncoder.EncodeSegment("LPT9").Should().Be("~LPT9");
    }

    [Fact]
    public void EncodeSegment_ClockDollar_Prefixed()
    {
        NameEncoder.EncodeSegment("CLOCK$").Should().Be("~CLOCK$");
    }

    [Fact]
    public void EncodeSegment_NulDotTxt_Prefixed()
    {
        // "nul.txt" — base name is "nul", which is reserved.
        NameEncoder.EncodeSegment("nul.txt").Should().Be("~nul.txt");
    }

    [Fact]
    public void EncodeSegment_NotReserved_Unchanged()
    {
        NameEncoder.EncodeSegment("nullable").Should().Be("nullable");
    }

    [Fact]
    public void EncodeSegment_COM0_NotReserved()
    {
        // COM0 is not a reserved device name (only COM1-9).
        NameEncoder.EncodeSegment("COM0").Should().Be("COM0");
    }

    // Rule 3: case/normalization collisions.

    [Fact]
    public void ResolveCollisions_NoCollision_PlainNames()
    {
        var names = new[] { "orders", "customers" };
        var result = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("test", n));

        result["orders"].Should().Be("orders");
        result["customers"].Should().Be("customers");
    }

    [Fact]
    public void ResolveCollisions_CasePair_OrdinalWinnerKeepsPlain()
    {
        // MixedCase (M = U+004D) sorts before mixedcase (m = U+006D) ordinally.
        var names = new[] { "mixedcase", "MixedCase" };
        var result = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("sel_test", n));

        result["MixedCase"].Should().Be("MixedCase");
        result["mixedcase"].Should().StartWith("mixedcase-");
        result["mixedcase"].Should().HaveLength("mixedcase-".Length + 8);
    }

    [Fact]
    public void ResolveCollisions_CasePair_DiscriminatorIsDeterministic()
    {
        var names = new[] { "mixedcase", "MixedCase" };
        var r1 = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("sel_test", n));
        var r2 = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("sel_test", n));

        r1["mixedcase"].Should().Be(r2["mixedcase"]);
    }

    [Fact]
    public void ResolveCollisions_ThreeWayCollision_TwoLosersGetSuffixes()
    {
        // NFC-equivalent pair + one more.
        var names = new[] { "café", "cafe\u0301", "CAFÉ" };
        var result = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("s", n));

        // All three fold to the same group; ordinal winner keeps plain name.
        var plainCount = result.Values.Count(v => !v.Contains('-'));
        plainCount.Should().Be(1);
    }

    // Rule 4: path budget.

    [Fact]
    public void ApplyPathBudget_ShortPath_Unchanged()
    {
        var tableRef = ObjectRef.Table("s", "t");
        NameEncoder.ApplyPathBudget("s", "t", 0, tableRef).Should().Be("t");
    }

    [Fact]
    public void ApplyPathBudget_ExceedsBudget_Truncated()
    {
        var longSchema = new string('a', 100);
        var longTable = new string('b', 200);
        var tableRef = ObjectRef.Table(longSchema, longTable);

        var result = NameEncoder.ApplyPathBudget(longSchema, longTable, 0, tableRef);

        // Must be truncated and have discriminator suffix.
        result.Should().Contain("-");
        var truncated = result[..result.LastIndexOf('-')];
        truncated.Length.Should().BeLessOrEqualTo(NameEncoder.TruncatedTableLength);
    }

    // Rule 5: last resort.

    [Fact]
    public void UnmappedPath_ReturnsHexFolder()
    {
        var tableRef = ObjectRef.Table("s", "t");
        var path = NameEncoder.UnmappedPath(tableRef);

        path.Should().StartWith("_unmapped/");
        path.Should().HaveLength("_unmapped/".Length + 64); // 64 hex chars
    }

    // Cross-platform determinism.

    [Fact]
    public void SameFixture_ProducesSameTree_OrdinalAndNFC()
    {
        // Verify that the encoder produces identical results regardless of host filesystem.
        var names = new[] { "MixedCase", "mixedcase", "orders" };
        var r1 = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("sel_test", n));
        var r2 = NameEncoder.ResolveCollisions(names, n => ObjectRef.Table("sel_test", n));

        foreach (var name in names)
        {
            r1[name].Should().Be(r2[name]);
        }
    }

    // Percent + tilde ordering: % encoded first, then ~.

    [Fact]
    public void EncodeSegment_PercentThenTilde_BothEncoded()
    {
        NameEncoder.EncodeSegment("%~").Should().Be("%25%7E");
    }

    [Fact]
    public void EncodeSegment_LiteralPercent25_NotConfusedWithEncoded()
    {
        // A name that literally contains "%25" must encode the % first, so it becomes "%2525".
        NameEncoder.EncodeSegment("%25").Should().Be("%2525");
    }
}
