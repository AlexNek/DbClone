using CanonicalModel.Interchange;
using FluentAssertions;
using Xunit;

namespace DatabaseInterchange.Tests.Format;

/// <summary>
/// Vectors for <see cref="ObjectRef"/> — the qualified-name reference type that replaces
/// content-derived hashes (D1).
/// </summary>
public sealed class ObjectRefTests
{
    [Fact]
    public void Schema_PlainName_NoQuoting()
    {
        var r = ObjectRef.Schema("copy_test");
        r.Value.Should().Be("schema:copy_test");
    }

    [Fact]
    public void Table_PlainNames_NoQuoting()
    {
        var r = ObjectRef.Table("sel_test", "orders");
        r.Value.Should().Be("table:sel_test.orders");
    }

    [Fact]
    public void Table_NameWithSpaces_Quoted()
    {
        var r = ObjectRef.Table("sel_test", "Weird Name");
        r.Value.Should().Be("table:sel_test.\"Weird Name\"");
    }

    [Fact]
    public void Table_NameStartingWithDigit_Quoted()
    {
        var r = ObjectRef.Table("public", "1test");
        r.Value.Should().Be("table:public.\"1test\"");
    }

    [Fact]
    public void Table_EmptyName_QuotedAsEmpty()
    {
        var r = ObjectRef.Table("public", "");
        r.Value.Should().Be("table:public.\"\"");
    }

    [Fact]
    public void Table_NameWithDoubleQuote_QuotedAndDoubled()
    {
        var r = ObjectRef.Table("public", "a\"b");
        r.Value.Should().Be("table:public.\"a\"\"b\"");
    }

    [Fact]
    public void Column_ThreeParts()
    {
        var r = ObjectRef.Column("sel_test", "orders", "id");
        r.Value.Should().Be("column:sel_test.orders.id");
    }

    [Fact]
    public void Column_QuotedParts()
    {
        var r = ObjectRef.Column("sel_test", "Weird Name", "order");
        r.Value.Should().Be("column:sel_test.\"Weird Name\".order");
    }

    [Fact]
    public void Routine_WithSignature()
    {
        var r = ObjectRef.Routine("public", "calc", "integer,text");
        r.Value.Should().Be("routine:public.calc(integer,text)");
    }

    [Fact]
    public void Routine_WithoutSignature()
    {
        var r = ObjectRef.Routine("public", "log_audit");
        r.Value.Should().Be("routine:public.log_audit");
    }

    [Fact]
    public void Extension_NoSchema()
    {
        var r = ObjectRef.Extension("pgcrypto");
        r.Value.Should().Be("extension:pgcrypto");
    }

    [Fact]
    public void Sequence_PlainNames()
    {
        var r = ObjectRef.Sequence("copy_test", "t_id_seq");
        r.Value.Should().Be("sequence:copy_test.t_id_seq");
    }

    [Fact]
    public void View_PlainNames()
    {
        var r = ObjectRef.View("sel_test", "v_order_totals");
        r.Value.Should().Be("view:sel_test.v_order_totals");
    }

    [Fact]
    public void Kind_ReturnsPrefix()
    {
        ObjectRef.Table("a", "b").Kind.Should().Be("table");
        ObjectRef.Schema("x").Kind.Should().Be("schema");
        ObjectRef.Column("a", "b", "c").Kind.Should().Be("column");
    }

    [Fact]
    public void ObjectName_ReturnsLastSegment()
    {
        ObjectRef.Table("sel_test", "orders").ObjectName().Should().Be("orders");
        ObjectRef.Column("a", "b", "c").ObjectName().Should().Be("c");
        ObjectRef.Routine("public", "calc", "integer").ObjectName().Should().Be("calc");
        ObjectRef.Schema("copy_test").ObjectName().Should().Be("copy_test");
    }

    [Fact]
    public void SchemaName_ReturnsFirstSegmentOrNull()
    {
        ObjectRef.Table("sel_test", "orders").SchemaName().Should().Be("sel_test");
        ObjectRef.Schema("copy_test").SchemaName().Should().BeNull();
        ObjectRef.Extension("pgcrypto").SchemaName().Should().BeNull();
    }

    [Fact]
    public void Comparison_IsOrdinalCaseSensitive()
    {
        var lower = ObjectRef.Table("sel_test", "mixedcase");
        var upper = ObjectRef.Table("sel_test", "MixedCase");
        lower.Should().NotBe(upper);
        lower.CompareTo(upper).Should().BeGreaterThan(0); // 'm' > 'M' ordinally
    }

    [Fact]
    public void Equality_IsOrdinalCaseSensitive()
    {
        var a = ObjectRef.Table("s", "t");
        var b = ObjectRef.Table("s", "t");
        a.Should().Be(b);

        var c = ObjectRef.Table("s", "T");
        a.Should().NotBe(c);
    }

    [Fact]
    public void DiscriminatorHex_Returns8HexChars()
    {
        var r = ObjectRef.Table("sel_test", "mixedcase");
        var hex = r.DiscriminatorHex();
        hex.Should().HaveLength(8);
        hex.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    [Fact]
    public void DiscriminatorHex_IsDeterministic()
    {
        var r1 = ObjectRef.Table("sel_test", "mixedcase");
        var r2 = ObjectRef.Table("sel_test", "mixedcase");
        r1.DiscriminatorHex().Should().Be(r2.DiscriminatorHex());
    }

    [Fact]
    public void DiscriminatorHex_DiffersForDifferentRefs()
    {
        var r1 = ObjectRef.Table("sel_test", "mixedcase");
        var r2 = ObjectRef.Table("sel_test", "MixedCase");
        r1.DiscriminatorHex().Should().NotBe(r2.DiscriminatorHex());
    }

    [Fact]
    public void SubObject_Index()
    {
        var r = ObjectRef.SubObject("index", "sel_test", "orders", "idx_orders_customer");
        r.Value.Should().Be("index:sel_test.orders.idx_orders_customer");
    }

    [Fact]
    public void SubObject_Trigger()
    {
        var r = ObjectRef.SubObject("trigger", "public", "users", "t_audit");
        r.Value.Should().Be("trigger:public.users.t_audit");
    }

    [Fact]
    public void Role_PlainName()
    {
        var r = ObjectRef.Role("postgres");
        r.Value.Should().Be("role:postgres");
    }

    [Fact]
    public void Policy_ThreeParts()
    {
        var r = ObjectRef.Policy("public", "users", "user_select");
        r.Value.Should().Be("policy:public.users.user_select");
    }
}
