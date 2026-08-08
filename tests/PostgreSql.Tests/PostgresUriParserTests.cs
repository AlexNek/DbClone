using DbClone.PostgreSql.Formats;

using FluentAssertions;

namespace PostgreSql.Tests;

/// <summary>
/// Unit tests for the tolerant <see cref="PostgresUriParser"/> that handles
/// unencoded special characters in passwords.
/// </summary>
public class PostgresUriParserTests
{
    [Fact]
    public void TryParse_StandardUri_ParsesCorrectly()
    {
        var result = PostgresUriParser.TryParse("postgresql://admin:s3cret@db.example.com:5433/sales");

        result.Should().NotBeNull();
        result!.Host.Should().Be("db.example.com");
        result.Port.Should().Be(5433);
        result.Database.Should().Be("sales");
        result.Username.Should().Be("admin");
        result.Password.Should().Be("s3cret");
    }

    [Fact]
    public void TryParse_PercentEncodedPassword_DecodesCorrectly()
    {
        var result = PostgresUriParser.TryParse("postgresql://user:p%40ss%23word@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@ss#word");
        result.Host.Should().Be("host.example.com");
        result.Username.Should().Be("user");
    }

    [Fact]
    public void TryParse_UnencodedAtInPassword_ParsesCorrectly()
    {
        // This is the main scenario: user pastes URI with unencoded @ in password
        var result = PostgresUriParser.TryParse("postgres://user:p@ss@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@ss");
        result.Host.Should().Be("host.example.com");
        result.Port.Should().Be(5432);
        result.Database.Should().Be("mydb");
        result.Username.Should().Be("user");
    }

    [Fact]
    public void TryParse_UnencodedHashInPassword_ParsesCorrectly()
    {
        var result = PostgresUriParser.TryParse("postgres://user:p#ssword@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p#ssword");
        result.Host.Should().Be("host.example.com");
        result.Database.Should().Be("mydb");
    }

    [Fact]
    public void TryParse_UnencodedAtAndHash_ParsesCorrectly()
    {
        // password is p@ss#word
        var result = PostgresUriParser.TryParse("postgres://user:p@ss#word@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@ss#word");
        result.Host.Should().Be("host.example.com");
        result.Port.Should().Be(5432);
        result.Database.Should().Be("mydb");
    }

    [Fact]
    public void TryParse_MultipleAtSignsInPassword_ParsesCorrectly()
    {
        // password is p@@ss
        var result = PostgresUriParser.TryParse("postgres://user:p@@ss@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@@ss");
        result.Host.Should().Be("host.example.com");
    }

    [Fact]
    public void TryParse_ColonInPassword_ParsesCorrectly()
    {
        // password is a:b:c
        var result = PostgresUriParser.TryParse("postgres://user:a:b:c@host.example.com:5432/db");

        result.Should().NotBeNull();
        result!.Password.Should().Be("a:b:c");
        result.Host.Should().Be("host.example.com");
        result.Username.Should().Be("user");
    }

    [Fact]
    public void TryParse_NoPassword_ReturnsNullPassword()
    {
        var result = PostgresUriParser.TryParse("postgresql://user@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Username.Should().Be("user");
        result.Password.Should().BeNull();
    }

    [Fact]
    public void TryParse_NoPort_DefaultsTo5432()
    {
        var result = PostgresUriParser.TryParse("postgresql://user:pass@host.example.com/mydb");

        result.Should().NotBeNull();
        result!.Port.Should().Be(5432);
    }

    [Fact]
    public void TryParse_WithSslModeQuery_ParsesQueryParams()
    {
        var result = PostgresUriParser.TryParse(
            "postgresql://user:pass@host.example.com:5432/mydb?sslmode=require");

        result.Should().NotBeNull();
        result!.QueryParams.Should().ContainKey("sslmode").WhoseValue.Should().Be("require");
    }

    [Fact]
    public void TryParse_WithMultipleQueryParams_ParsesAll()
    {
        var result = PostgresUriParser.TryParse(
            "postgresql://user:pass@host.example.com:5432/mydb?sslmode=require&application_name=test");

        result.Should().NotBeNull();
        result!.QueryParams.Should().ContainKey("sslmode").WhoseValue.Should().Be("require");
        result.QueryParams.Should().ContainKey("application_name").WhoseValue.Should().Be("test");
    }

    [Fact]
    public void TryParse_PostgresScheme_Works()
    {
        var result = PostgresUriParser.TryParse("postgres://user:pass@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Scheme.Should().Be("postgres");
    }

    [Fact]
    public void TryParse_PostgresqlScheme_Works()
    {
        var result = PostgresUriParser.TryParse("postgresql://user:pass@host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Scheme.Should().Be("postgresql");
    }

    [Fact]
    public void TryParse_InvalidScheme_ReturnsNull()
    {
        var result = PostgresUriParser.TryParse("mysql://user:pass@host.example.com:3306/mydb");

        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsNull()
    {
        var result = PostgresUriParser.TryParse("");

        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_NullString_ReturnsNull()
    {
        var result = PostgresUriParser.TryParse(null!);

        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_PasswordWithSpaces_ParsesCorrectly()
    {
        // Spaces encoded
        var result = PostgresUriParser.TryParse("postgres://user:my%20pass%20word@host.example.com:5432/db");

        result.Should().NotBeNull();
        result!.Password.Should().Be("my pass word");
    }

    [Fact]
    public void TryParse_PasswordWithExclamationAndPercent_ParsesCorrectly()
    {
        // password: Xk9#mZ!qR4w
        var result = PostgresUriParser.TryParse("postgres://user:Xk9#mZ!qR4w@host.example.com:5432/db");

        result.Should().NotBeNull();
        result!.Password.Should().Be("Xk9#mZ!qR4w");
    }

    [Fact]
    public void TryParse_SupabaseStyleUri_ParsesCorrectly()
    {
        var result = PostgresUriParser.TryParse(
            "postgres://postgres.fakeprojectref:MyP@ss!@aws-0-us-east-1.pooler.supabase.co:6543/postgres");

        result.Should().NotBeNull();
        result!.Username.Should().Be("postgres.fakeprojectref");
        result.Password.Should().Be("MyP@ss!");
        result.Host.Should().Be("aws-0-us-east-1.pooler.supabase.co");
        result.Port.Should().Be(6543);
        result.Database.Should().Be("postgres");
    }

    [Fact]
    public void TryParse_AlreadyEncodedInput_DoesNotDoubleEncode()
    {
        // Already percent-encoded — should decode correctly, not double-decode
        var result = PostgresUriParser.TryParse("postgres://user:p%2540ss@host.example.com:5432/db");

        result.Should().NotBeNull();
        // %2540 → first decode gives %40 (since %25 is literal %), which stays as %40
        // Actually: %25 decodes to %, so %2540 decodes to %40, then we don't re-decode
        result!.Password.Should().Be("p%40ss");
    }

    [Fact]
    public void TryParse_NoUserInfo_DefaultsToNull()
    {
        var result = PostgresUriParser.TryParse("postgresql://host.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Username.Should().BeNull();
        result.Password.Should().BeNull();
    }

    [Fact]
    public void TryParse_IPv6Host_ParsesHostWithoutBrackets()
    {
        var result = PostgresUriParser.TryParse("postgres://user:pw@[2001:db8::1]:5432/db");

        result.Should().NotBeNull();
        result!.Host.Should().Be("2001:db8::1");
        result.Port.Should().Be(5432);
        result.Database.Should().Be("db");
        result.Username.Should().Be("user");
        result.Password.Should().Be("pw");
    }

    [Fact]
    public void TryParse_IPv6HostWithSpecialCharPassword_ParsesCorrectly()
    {
        // Unencoded @ in password must not derail the right-to-left host scan
        var result = PostgresUriParser.TryParse("postgres://user:p@ss@[2001:db8::1]:5432/db");

        result.Should().NotBeNull();
        result!.Host.Should().Be("2001:db8::1");
        result.Password.Should().Be("p@ss");
        result.Database.Should().Be("db");
    }

    [Fact]
    public void TryParse_IPv6HostWithoutPort_DefaultsTo5432()
    {
        var result = PostgresUriParser.TryParse("postgres://user:pw@[2001:db8::1]/db");

        result.Should().NotBeNull();
        result!.Host.Should().Be("2001:db8::1");
        result.Port.Should().Be(5432);
    }

    [Fact]
    public void TryParse_AtInPasswordAndQueryValue_PicksHostSplit()
    {
        // Hard case: the '@' in the query value sits rightmost, but the richer
        // host candidate (dot + port + path) with clean user-info must win
        var result = PostgresUriParser.TryParse(
            "postgres://user:p@ss@db.example.com:5432/mydb?opt=a@b");

        result.Should().NotBeNull();
        result!.Username.Should().Be("user");
        result.Password.Should().Be("p@ss");
        result.Host.Should().Be("db.example.com");
        result.Port.Should().Be(5432);
        result.Database.Should().Be("mydb");
        result.QueryParams.Should().ContainKey("opt").WhoseValue.Should().Be("a@b");
    }

    [Fact]
    public void TryParse_RichLookingQueryValue_StillPicksHostSplit()
    {
        // Harder: the query value itself looks like host:port/path, so host
        // richness alone cannot decide — the swallowed-structure penalty must
        var result = PostgresUriParser.TryParse(
            "postgres://user:p@ss@db.example.com:5432/mydb?opt=a@b.example.com:5433/x");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@ss");
        result.Host.Should().Be("db.example.com");
        result.Database.Should().Be("mydb");
        result.QueryParams.Should().ContainKey("opt")
            .WhoseValue.Should().Be("a@b.example.com:5433/x");
    }

    [Fact]
    public void TryParse_DigitPasswordWithQuestionMark_ParsesCorrectly()
    {
        // Hard case: password starts with digits and contains '?' — must not be
        // truncated by a query cut, and 'user:5432' must not read as host:port
        var result = PostgresUriParser.TryParse(
            "postgres://user:5432?secret@db.example.com:5432/mydb");

        result.Should().NotBeNull();
        result!.Username.Should().Be("user");
        result.Password.Should().Be("5432?secret");
        result.Host.Should().Be("db.example.com");
        result.Database.Should().Be("mydb");
    }

    [Fact]
    public void TryParse_QuestionMarkPasswordWithRealQuery_ParsesBoth()
    {
        var result = PostgresUriParser.TryParse(
            "postgres://user:p?ss@db.example.com:5432/mydb?sslmode=require");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p?ss");
        result.Host.Should().Be("db.example.com");
        result.QueryParams.Should().ContainKey("sslmode").WhoseValue.Should().Be("require");
    }

    [Fact]
    public void TryParse_AtInQueryValue_HostWithoutPortOrDot_PicksHostSplit()
    {
        // Hard case: host carries only a database path as richness signal
        var result = PostgresUriParser.TryParse("postgres://user:pass@db/db?opt=a@b");

        result.Should().NotBeNull();
        result!.Password.Should().Be("pass");
        result.Host.Should().Be("db");
        result.Database.Should().Be("db");
        result.QueryParams.Should().ContainKey("opt").WhoseValue.Should().Be("a@b");
    }

    [Fact]
    public void TryParse_IPv6WithAtInQueryValue_PicksHostSplit()
    {
        var result = PostgresUriParser.TryParse(
            "postgres://user:p@ss@[2001:db8::1]:5432/db?opt=a@b");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@ss");
        result.Host.Should().Be("2001:db8::1");
        result.Port.Should().Be(5432);
        result.QueryParams.Should().ContainKey("opt").WhoseValue.Should().Be("a@b");
    }

    [Fact]
    public void TryParse_AtAndHashPasswordWithAtInQuery_ParsesCorrectly()
    {
        // Hard case: password mixes '@', '#' and '&' while the query also holds '@'
        var result = PostgresUriParser.TryParse(
            "postgres://user:p@ss#w&rd@db.example.com:5432/mydb?x=y@z");

        result.Should().NotBeNull();
        result!.Password.Should().Be("p@ss#w&rd");
        result.Host.Should().Be("db.example.com");
        result.Database.Should().Be("mydb");
        result.QueryParams.Should().ContainKey("x").WhoseValue.Should().Be("y@z");
    }

    [Theory]
    [InlineData("postgres://user:p@ss#word@host.example.com:5432/mydb", "p@ss#word")]
    [InlineData("postgres://user:p%40ss%23word@host.example.com:5432/mydb", "p@ss#word")]
    [InlineData("postgres://user:simple@host.example.com:5432/mydb", "simple")]
    [InlineData("postgres://user:a:b:c@host.example.com:5432/mydb", "a:b:c")]
    [InlineData("postgres://user:pass@word@host.example.com:5432/mydb", "pass@word")]
    [InlineData("postgres://user:p@ss:w#rd@host.example.com:5432/mydb", "p@ss:w#rd")]
    [InlineData("postgres://user:p&ss&word@host.example.com:5432/mydb", "p&ss&word")]
    [InlineData("postgres://user:p?ss?word@host.example.com:5432/mydb", "p?ss?word")]
    [InlineData("postgres://user:p/ss/word@host.example.com:5432/mydb", "p/ss/word")]
    [InlineData("postgres://user:p%ss@host.example.com:5432/mydb", "p%ss")]
    [InlineData("postgres://user:p@ss#w&rd?!@host.example.com:5432/mydb", "p@ss#w&rd?!")]
    [InlineData("postgres://user:@host.example.com:5432/mydb", "")]
    [InlineData("postgres://user: @host.example.com:5432/mydb", " ")]
    public void TryParse_VariousPasswords_ParsedCorrectly(string input, string expectedPassword)
    {
        var result = PostgresUriParser.TryParse(input);

        result.Should().NotBeNull();
        result!.Password.Should().Be(expectedPassword);
        result.Host.Should().Be("host.example.com");
        result.Port.Should().Be(5432);
    }

    [Theory]
    [InlineData("postgres://user:p&ss@host.example.com:5432/mydb?sslmode=require", "p&ss", "require")]
    [InlineData("postgres://user:p@ss#word@host.example.com:5432/mydb?sslmode=disable", "p@ss#word", "disable")]
    [InlineData("postgres://user:a?b&c#d@host.example.com:5432/mydb?sslmode=verify-full", "a?b&c#d", "verify-full")]
    public void TryParse_SpecialPasswordWithQuery_BothParsedCorrectly(
        string input,
        string expectedPassword,
        string expectedSslMode)
    {
        var result = PostgresUriParser.TryParse(input);

        result.Should().NotBeNull();
        result!.Password.Should().Be(expectedPassword);
        result.Host.Should().Be("host.example.com");
        result.QueryParams.Should().ContainKey("sslmode").WhoseValue.Should().Be(expectedSslMode);
    }
}
