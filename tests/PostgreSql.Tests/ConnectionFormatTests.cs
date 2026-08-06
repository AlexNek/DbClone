using DbClone.Application.Enums;
using DbClone.Application.Models;
using DbClone.PostgreSql.Formats;

using FluentAssertions;

namespace PostgreSql.Tests;

/// <summary>
/// Unit tests for the individual <c>IConnectionFormat</c> implementations.
/// Each format is exercised directly (Parse / Export / CanImport / CanExport)
/// so that parsing behaviour is validated independently of detection ordering.
/// </summary>
public class PostgreSqlUriFormatTests
{
    private readonly PostgreSqlUriFormat _sut = new();

    [Fact]
    public void CanImport_JdbcString_ReturnsFalse()
    {
        // Arrange
        var input = "jdbc:postgresql://localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_KeyValueString_ReturnsFalse()
    {
        // Arrange
        var input = "Host=localhost;Port=5432";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_PostgresqlScheme_ReturnsTrue()
    {
        // Arrange
        var input = "postgresql://user:pass@localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_PostgresScheme_ReturnsTrue()
    {
        // Arrange
        var input = "postgres://user:pass@localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Export_DefaultSsl_OmitsSslParam()
    {
        // Arrange
        var conn = new DatabaseConnection
                       {
                           Host = "localhost",
                           Port = 5432,
                           Database = "db",
                           Username = "user",
                           Password = "pass",
                           SslMode = ESslMode.Prefer
                       };

        // Act
        var result = _sut.Export(conn);

        // Assert
        result.Should().Be("postgresql://user:pass@localhost:5432/db");
    }

    [Fact]
    public void Export_FullConnection_ProducesValidUri()
    {
        // Arrange
        var conn = new DatabaseConnection
                       {
                           Provider = EDatabaseProvider.PostgreSql,
                           Host = "db.example.com",
                           Port = 5433,
                           Database = "sales",
                           Username = "admin",
                           Password = "s3cret",
                           SslMode = ESslMode.Require
                       };

        // Act
        var result = _sut.Export(conn);

        // Assert
        result.Should().Be("postgresql://admin:s3cret@db.example.com:5433/sales?sslmode=require");
    }

    [Fact]
    public void Parse_FullUri_ExtractsAllFields()
    {
        // Arrange
        var input = "postgresql://admin:s3cret@db.example.com:5433/sales?sslmode=require";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Provider.Should().Be(EDatabaseProvider.PostgreSql);
        conn.Host.Should().Be("db.example.com");
        conn.Port.Should().Be(5433);
        conn.Database.Should().Be("sales");
        conn.Username.Should().Be("admin");
        conn.Password.Should().Be("s3cret");
        conn.SslMode.Should().Be(ESslMode.Require);
    }

    [Fact]
    public void Parse_NoPassword_PasswordIsNull()
    {
        // Arrange
        var input = "postgresql://user@localhost:5432/mydb";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Username.Should().Be("user");
        conn.Password.Should().BeNull();
    }

    [Fact]
    public void Parse_NoPort_DefaultsTo5432()
    {
        // Arrange
        var input = "postgresql://user:pass@localhost/mydb";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Port.Should().Be(5432);
    }

    [Theory]
    [InlineData("disable", ESslMode.Disable)]
    [InlineData("prefer", ESslMode.Prefer)]
    [InlineData("require", ESslMode.Require)]
    [InlineData("verify-ca", ESslMode.VerifyCA)]
    [InlineData("verify-full", ESslMode.VerifyFull)]
    public void Parse_SslModes_MappedCorrectly(string sslValue, ESslMode expected)
    {
        // Arrange
        var input = $"postgresql://user:pass@localhost:5432/mydb?sslmode={sslValue}";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.SslMode.Should().Be(expected);
    }

    [Fact]
    public void Parse_Then_Export_RoundTrip_PreservesFields()
    {
        // Arrange
        var original = _sut.Parse(
            "postgresql://admin:s3cret@db.example.com:5433/sales?sslmode=require");

        // Act
        var reparsed = _sut.Parse(_sut.Export(original));

        // Assert
        reparsed.Host.Should().Be(original.Host);
        reparsed.Port.Should().Be(original.Port);
        reparsed.Database.Should().Be(original.Database);
        reparsed.Username.Should().Be(original.Username);
        reparsed.Password.Should().Be(original.Password);
        reparsed.SslMode.Should().Be(original.SslMode);
    }

    [Fact]
    public void Parse_UnknownQueryParams_PreservedInOptions()
    {
        // Arrange
        var input =
            "postgresql://user:pass@localhost:5432/mydb?ApplicationName=myapp&ConnectTimeout=10";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainKey("ApplicationName").WhoseValue.Should().Be("myapp");
        conn.Options.Should().ContainKey("ConnectTimeout").WhoseValue.Should().Be("10");
    }

    [Fact]
    public void Parse_UrlEncodedPassword_DecodesPassword()
    {
        // Arrange
        var input = "postgresql://user:p%40ss%3Aw0rd@localhost:5432/mydb";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Password.Should().Be("p@ss:w0rd");
    }
}

public class PostgreSqlNpgsqlFormatTests
{
    private readonly PostgreSqlNpgsqlFormat _sut = new();

    [Fact]
    public void CanImport_DatabaseKeyWithoutHost_ReturnsTrue()
    {
        // Arrange
        var input = "Hosta=localhost;Port=5432;Database=mydb;Username=user";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_LibpqSpaceSeparated_ReturnsFalse()
    {
        // Arrange
        var input = "host=localhost port=5432 dbname=mydb";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_PortKeyOnly_ReturnsTrue()
    {
        // Arrange
        var input = "Hosta=localhost;Port=5432;Db=mydb";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_SemicolonKeyValue_ReturnsTrue()
    {
        // Arrange
        var input = "Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_ServerKeyword_ReturnsTrue()
    {
        // Arrange
        var input = "Server=localhost;Database=mydb";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_UriString_ReturnsFalse()
    {
        // Arrange
        var input = "postgresql://user:pass@localhost/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Export_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new DatabaseConnection
                           {
                               Provider = EDatabaseProvider.PostgreSql,
                               Host = "db.example.com",
                               Port = 5433,
                               Database = "sales",
                               Username = "admin",
                               Password = "s3cret",
                               SslMode = ESslMode.Require
                           };

        // Act
        var reparsed = _sut.Parse(_sut.Export(original));

        // Assert
        reparsed.Host.Should().Be("db.example.com");
        reparsed.Port.Should().Be(5433);
        reparsed.Database.Should().Be("sales");
        reparsed.Username.Should().Be("admin");
        reparsed.Password.Should().Be("s3cret");
        reparsed.SslMode.Should().Be(ESslMode.Require);
    }

    [Fact]
    public void Parse_ApplicationName_PreservedInOptions()
    {
        // Arrange
        var input = "Host=localhost;Database=db;Username=u;Application Name=myapp";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainKey("Application Name").WhoseValue.Should().Be("myapp");
    }

    [Fact]
    public void Parse_ExplicitPooling_PreservedInOptions()
    {
        // Arrange
        var input = "Host=localhost;Database=db;Username=u;Pooling=false";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainKey("Pooling").WhoseValue.Should().Be("False");
    }

    [Fact]
    public void Parse_ExplicitTimeout_PreservedInOptions()
    {
        // Arrange
        var input = "Host=localhost;Database=db;Username=u;Timeout=30";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainKey("Timeout").WhoseValue.Should().Be("30");
    }

    [Fact]
    public void Parse_KeyValue_ExtractsAllFields()
    {
        // Arrange
        var input =
            "Host=db.example.com;Port=5433;Database=sales;Username=admin;Password=s3cret;SslMode=Require";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.example.com");
        conn.Port.Should().Be(5433);
        conn.Database.Should().Be("sales");
        conn.Username.Should().Be("admin");
        conn.Password.Should().Be("s3cret");
        conn.SslMode.Should().Be(ESslMode.Require);
    }

    [Fact]
    public void Parse_NoExtraOptions_OptionsIsEmpty()
    {
        // Arrange
        var input = "Host=localhost;Port=5432;Database=db;Username=user;Password=pass";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().BeEmpty();
    }

    [Fact]
    public void Parse_OnlySearchPath_DoesNotLeakTimeoutOrPooling()
    {
        // Arrange
        var input =
            "Host=localhost;Port=5432;Database=db;Username=user;Password=pass;Search Path=public";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainSingle()
            .Which.Key.Should().Be("Search Path");
        conn.Options["Search Path"].Should().Be("public");
    }

    [Theory]
    [InlineData("Xk9#mZ!qR4w")]
    [InlineData("p@ss?word!")]
    [InlineData("a&b#c!d%e")]
    [InlineData("pass with spaces")]
    public void Parse_PasswordWithSpecialCharacters_PreservesPassword(string password)
    {
        // Arrange
        var input = $"Host=localhost;Port=5432;Database=db;Username=user;Password={password}";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Password.Should().Be(password);
    }
}

public class PostgreSqlJdbcFormatTests
{
    private readonly PostgreSqlJdbcFormat _sut = new();

    [Fact]
    public void CanImport_JdbcPrefix_ReturnsTrue()
    {
        // Arrange
        var input = "jdbc:postgresql://localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_PlainUri_ReturnsFalse()
    {
        // Arrange
        var input = "postgresql://localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Export_ProducesJdbcString()
    {
        // Arrange
        var conn = new DatabaseConnection
                       {
                           Host = "db.example.com",
                           Port = 5433,
                           Database = "sales",
                           Username = "admin",
                           Password = "s3cret",
                           SslMode = ESslMode.Require
                       };

        // Act
        var result = _sut.Export(conn);

        // Assert
        result.Should().StartWith("jdbc:postgresql://db.example.com:5433/sales?user=admin");
        result.Should().Contain("password=s3cret");
        result.Should().Contain("sslmode=require");
    }

    [Fact]
    public void Parse_JdbcWithQueryParams_ExtractsAllFields()
    {
        // Arrange
        var input =
            "jdbc:postgresql://db.example.com:5433/sales?user=admin&password=s3cret&sslmode=require";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.example.com");
        conn.Port.Should().Be(5433);
        conn.Database.Should().Be("sales");
        conn.Username.Should().Be("admin");
        conn.Password.Should().Be("s3cret");
        conn.SslMode.Should().Be(ESslMode.Require);
    }

    [Fact]
    public void Parse_SslTrueParam_MapsToRequire()
    {
        // Arrange
        var input = "jdbc:postgresql://localhost:5432/db?user=u&ssl=true";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.SslMode.Should().Be(ESslMode.Require);
    }

    [Fact]
    public void Parse_UnknownParams_PreservedInOptions()
    {
        // Arrange
        var input = "jdbc:postgresql://localhost:5432/db?user=u&ApplicationName=myapp";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainKey("ApplicationName").WhoseValue.Should().Be("myapp");
    }
}

public class PostgreSqlLibpqFormatTests
{
    private readonly PostgreSqlLibpqFormat _sut = new();

    [Fact]
    public void CanImport_AdoNetUppercase_ReturnsFalse()
    {
        // Arrange
        var input = "Host=localhost;Port=5432;Database=mydb";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_EnvVarPghost_ReturnsFalse()
    {
        // Arrange
        var input = "PGHOST=localhost";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_SpaceSeparatedLowercase_ReturnsTrue()
    {
        // Arrange
        var input = "host=localhost port=5432 dbname=mydb user=postgres";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Export_ProducesSpaceSeparated()
    {
        // Arrange
        var conn = new DatabaseConnection
                       {
                           Host = "localhost",
                           Port = 5432,
                           Database = "db",
                           Username = "u",
                           Password = "p",
                           SslMode = ESslMode.Require
                       };

        // Act
        var result = _sut.Export(conn);

        // Assert
        result.Should().Contain("host=localhost");
        result.Should().Contain("port=5432");
        result.Should().Contain("dbname=db");
        result.Should().Contain("user=u");
        result.Should().Contain("password=p");
        result.Should().Contain("sslmode=require");
    }

    [Fact]
    public void Parse_QuotedPassword_HandlesSpaces()
    {
        // Arrange
        var input = "host=localhost dbname=db user=u password='my secret pass'";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Password.Should().Be("my secret pass");
    }

    [Fact]
    public void Parse_SpaceSeparated_ExtractsAllFields()
    {
        // Arrange
        var input =
            "host=db.example.com port=5433 dbname=sales user=admin password=s3cret sslmode=require";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.example.com");
        conn.Port.Should().Be(5433);
        conn.Database.Should().Be("sales");
        conn.Username.Should().Be("admin");
        conn.Password.Should().Be("s3cret");
        conn.SslMode.Should().Be(ESslMode.Require);
    }

    [Fact]
    public void Parse_UnknownKeys_PreservedInOptions()
    {
        // Arrange
        var input = "host=localhost dbname=db user=u application_name=myapp";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Options.Should().ContainKey("application_name").WhoseValue.Should().Be("myapp");
    }
}

public class PostgreSqlSupabaseFormatTests
{
    private const string SupabaseUri =
        "postgresql://postgres.testrefxyz:secret@aws-0-us-east-1.pooler.supabase.co:6543/postgres";

    private readonly PostgreSqlSupabaseFormat _sut = new();

    [Fact]
    public void CanImport_GenericUri_ReturnsFalse()
    {
        // Arrange
        var input = "postgresql://user:pass@localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_SupabaseHost_ReturnsTrue()
    {
        // Arrange
        var input = SupabaseUri;

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Parse_ExtractsProjectRef()
    {
        // Arrange
        var input = SupabaseUri;

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("aws-0-us-east-1.pooler.supabase.co");
        conn.Port.Should().Be(6543);
        conn.Database.Should().Be("postgres");
        conn.Username.Should().Be("postgres.testrefxyz");
        conn.Password.Should().Be("secret");
        conn.Options.Should().ContainKey("SupabaseProjectRef").WhoseValue.Should().Be("testrefxyz");
    }
}

public class PostgreSqlSupabaseEnvFormatTests
{
    private readonly PostgreSqlSupabaseEnvFormat _sut = new();

    [Fact]
    public void CanExport_AlwaysFalse()
    {
        // Arrange
        var connection = new DatabaseConnection();

        // Act
        var result = _sut.CanExport(connection);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_NextPublicSupabaseUrl_ReturnsTrue()
    {
        // Arrange
        var input = "NEXT_PUBLIC_SUPABASE_URL=https://abcdef.supabase.co";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_PlainPostgresUri_ReturnsFalse()
    {
        // Arrange
        var input = "postgresql://user:pass@localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_ViteSupabaseUrl_ReturnsTrue()
    {
        // Arrange
        var input = "VITE_SUPABASE_URL=https://fakeprojectref.supabase.co";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Parse_MultilineWithAnonKey_ExtractsProjectRef()
    {
        // Arrange
        var input =
            "VITE_SUPABASE_ANON_KEY=eyJhbGciOi...\nVITE_SUPABASE_URL=https://fakeprojectref.supabase.co";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.fakeprojectref.supabase.co");
        conn.Options.Should().ContainKey("SupabaseProjectRef").WhoseValue.Should()
            .Be("fakeprojectref");
    }

    [Fact]
    public void Parse_ViteSupabaseUrl_ExtractsProjectRef()
    {
        // Arrange
        var input = "VITE_SUPABASE_URL=https://fakeprojectref.supabase.co";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.fakeprojectref.supabase.co");
        conn.Port.Should().Be(5432);
        conn.Database.Should().Be("postgres");
        conn.Username.Should().Be("postgres");
        conn.Password.Should().BeNull();
        conn.SslMode.Should().Be(ESslMode.Require);
        conn.Options.Should().ContainKey("SupabaseProjectRef").WhoseValue.Should()
            .Be("fakeprojectref");
    }
}

public class PostgreSqlEnvVarFormatTests
{
    private readonly PostgreSqlEnvVarFormat _sut = new();

    [Fact]
    public void CanExport_AlwaysFalse()
    {
        // Arrange
        var connection = new DatabaseConnection();

        // Act
        var result = _sut.CanExport(connection);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanImport_DatabaseUrl_ReturnsTrue()
    {
        // Arrange
        var input = "DATABASE_URL=postgresql://user:pass@localhost:5432/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_PgHost_ReturnsTrue()
    {
        // Arrange
        var input = "PGHOST=localhost";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanImport_PlainUri_ReturnsFalse()
    {
        // Arrange
        var input = "postgresql://user:pass@localhost/db";

        // Act
        var result = _sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Export_ThrowsNotSupported()
    {
        // Arrange
        var connection = new DatabaseConnection();

        // Act
        var act = () => _sut.Export(connection);

        // Assert
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Parse_DatabaseUrl_ExtractsUri()
    {
        // Arrange
        var input = "DATABASE_URL=postgresql://admin:s3cret@db.example.com:5433/sales";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.example.com");
        conn.Port.Should().Be(5433);
        conn.Database.Should().Be("sales");
        conn.Username.Should().Be("admin");
        conn.Password.Should().Be("s3cret");
    }

    [Fact]
    public void Parse_PgEnvVars_ExtractsFields()
    {
        // Arrange
        var input =
            "PGHOST=db.example.com\nPGPORT=5433\nPGDATABASE=sales\nPGUSER=admin\nPGPASSWORD=s3cret";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.example.com");
        conn.Port.Should().Be(5433);
        conn.Database.Should().Be("sales");
        conn.Username.Should().Be("admin");
        conn.Password.Should().Be("s3cret");
    }

    [Fact]
    public void Parse_QuotedDatabaseUrl_StripsQuotes()
    {
        // Arrange
        var input = "DATABASE_URL=\"postgresql://admin:s3cret@db.example.com:5433/sales\"";

        // Act
        var conn = _sut.Parse(input);

        // Assert
        conn.Host.Should().Be("db.example.com");
    }
}

public class ExportOnlyFormatTests
{
    [Fact]
    public void Node_Export_ProducesJson()
    {
        // Arrange
        var sut = new PostgreSqlNodeFormat();
        var conn = SampleConnection();

        // Act
        var result = sut.Export(conn);

        // Assert
        result.Should().Contain("\"host\": \"db.example.com\"");
        result.Should().Contain("\"port\": 5433");
        result.Should().Contain("\"database\": \"sales\"");
        result.Should().Contain("\"user\": \"admin\"");
        result.Should().Contain("\"password\": \"s3cret\"");
        result.Should().Contain("\"ssl\": true");
    }

    [Fact]
    public void Node_Export_SslDisable_OmitsSsl()
    {
        // Arrange
        var sut = new PostgreSqlNodeFormat();
        var conn = SampleConnection();
        conn.SslMode = ESslMode.Disable;

        // Act
        var result = sut.Export(conn);

        // Assert
        result.Should().NotContain("\"ssl\"");
    }

    [Fact]
    public void Prisma_Export_IncludesSchemaPublic()
    {
        // Arrange
        var sut = new PostgreSqlPrismaFormat();
        var conn = SampleConnection();

        // Act
        var result = sut.Export(conn);

        // Assert
        result.Should().Be("postgresql://admin:s3cret@db.example.com:5433/sales?schema=public");
    }

    [Fact]
    public void Prisma_Export_UsesSchemaOption()
    {
        // Arrange
        var sut = new PostgreSqlPrismaFormat();
        var conn = SampleConnection();
        conn.Options["schema"] = "myschema";

        // Act
        var result = sut.Export(conn);

        // Assert
        result.Should().Be("postgresql://admin:s3cret@db.example.com:5433/sales?schema=myschema");
    }

    [Fact]
    public void SqlAlchemy_CanImport_AlwaysFalse()
    {
        // Arrange
        var sut = new PostgreSqlSqlAlchemyFormat();
        var input = "postgresql+psycopg2://u:p@h/db";

        // Act
        var result = sut.CanImport(input);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void SqlAlchemy_Export_ProducesPsycopg2Uri()
    {
        // Arrange
        var sut = new PostgreSqlSqlAlchemyFormat();
        var conn = SampleConnection();

        // Act
        var result = sut.Export(conn);

        // Assert
        result.Should().Be("postgresql+psycopg2://admin:s3cret@db.example.com:5433/sales");
    }

    private static DatabaseConnection SampleConnection() =>
        new()
            {
                Provider = EDatabaseProvider.PostgreSql,
                Host = "db.example.com",
                Port = 5433,
                Database = "sales",
                Username = "admin",
                Password = "s3cret",
                SslMode = ESslMode.Require
            };
}
