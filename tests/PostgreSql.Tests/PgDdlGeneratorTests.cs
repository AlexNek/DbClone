using DbClone.Application.Models;
using DbClone.PostgreSql.Ddl;

using FluentAssertions;

namespace PostgreSql.Tests;

public class PgDdlGeneratorTests
{
    private readonly PgDdlGenerator _generator = new();

    [Fact]
    public void GenerateCreateEnums_GeneratesCorrectDdl()
    {
        var enums = new List<EnumDefinition>
                        {
                            new("public", "status", ["active", "inactive"], null)
                        };

        var result = _generator.GenerateCreateEnums(enums);

        result.Should().HaveCount(1);
        result[0].Should().Contain("CREATE TYPE");
        result[0].Should().Contain("AS ENUM ('active', 'inactive')");
    }

    [Fact]
    public void GenerateCreateIndexes_AppendsTablespace_ToDefinition()
    {
        var indexes = new List<IndexDefinition>
                          {
                              new(
                                  "idx_data",
                                      ["created_at"],
                                  false,
                                  false,
                                  null,
                                  "fast_ssd",
                                  Definition:
                                  "CREATE INDEX idx_data ON public.events USING btree (created_at)")
                          };

        var result = _generator.GenerateCreateIndexes(indexes, "public", "events");

        result.Should().HaveCount(1);
        result[0].Should().EndWith("TABLESPACE fast_ssd;");
    }

    [Fact]
    public void GenerateCreateIndexes_SkipsConstraintBackedIndex()
    {
        var indexes = new List<IndexDefinition>
                          {
                              new(
                                  "users_email_key",
                                      ["email"],
                                  true,
                                  false,
                                  null,
                                  null,
                                  Definition:
                                  "CREATE UNIQUE INDEX users_email_key ON public.users USING btree (email)",
                                  IsConstraint: true),
                              new(
                                  "users_name_idx",
                                      ["name"],
                                  false,
                                  false,
                                  null,
                                  null,
                                  Definition:
                                  "CREATE INDEX users_name_idx ON public.users USING btree (name)")
                          };

        var result = _generator.GenerateCreateIndexes(indexes, "public", "users");

        result.Should().HaveCount(1); // Constraint-backed skipped
        result[0].Should().Contain("users_name_idx");
    }

    [Fact]
    public void GenerateCreateIndexes_SkipsPrimaryKey()
    {
        var indexes = new List<IndexDefinition>
                          {
                              new("users_pkey", ["id"], true, true, null, null),
                              new("users_email_idx", ["email"], true, false, null, null)
                          };

        var result = _generator.GenerateCreateIndexes(indexes, "public", "users");

        result.Should().HaveCount(1); // Primary key skipped
        result[0].Should().Contain("CREATE UNIQUE INDEX");
    }

    [Fact]
    public void GenerateCreateIndexes_UsesVerbatimDefinition_ForExpressionIndex()
    {
        var indexes = new List<IndexDefinition>
                          {
                              new(
                                  "users_lower_email_idx",
                                      ["lower(email::text)"],
                                  false,
                                  false,
                                  null,
                                  null,
                                  Definition:
                                  "CREATE INDEX users_lower_email_idx ON public.users USING btree (lower(email::text))")
                          };

        var result = _generator.GenerateCreateIndexes(indexes, "public", "users");

        result.Should().HaveCount(1);
        result[0].Should().Be(
            "CREATE INDEX users_lower_email_idx ON public.users USING btree (lower(email::text));");
    }

    [Fact]
    public void GenerateCreateSchemas_SimpleSchema()
    {
        var schemas = new List<SchemaDefinition> { new("my_schema", "postgres") };

        var result = _generator.GenerateCreateSchemas(schemas);

        result.Should().HaveCount(1);
        result[0].Should().Contain("CREATE SCHEMA IF NOT EXISTS my_schema");
    }

    [Fact]
    public void GenerateCreateSequences_GeneratesCorrectDdl()
    {
        var sequences = new List<SequenceDefinition>
                            {
                                new(
                                    "public",
                                    "users_id_seq",
                                    1,
                                    1,
                                    null,
                                    null,
                                    1,
                                    false,
                                    "integer",
                                    null)
                            };

        var result = _generator.GenerateCreateSequences(sequences);

        result.Should().HaveCount(1);
        result[0].Should().Contain("CREATE SEQUENCE");
        result[0].Should().Contain("START WITH 1");
    }

    [Fact]
    public void GenerateCreateTables_BasicTable()
    {
        var tables = new List<TableDefinition>
                         {
                             new(
                                 SchemaName: "public",
                                 Name: "users",
                                 Columns:
                                     [
                                         new ColumnDefinition(
                                             "id",
                                             "integer",
                                             1,
                                             false,
                                             "nextval('users_id_seq')",
                                             false,
                                             false,
                                             null,
                                             null),
                                         new ColumnDefinition(
                                             "name",
                                             "text",
                                             2,
                                             false,
                                             null,
                                             false,
                                             false,
                                             null,
                                             null),
                                         new ColumnDefinition(
                                             "email",
                                             "text",
                                             3,
                                             true,
                                             null,
                                             false,
                                             false,
                                             null,
                                             null)
                                     ],
                                 Indexes:
                                     [
                                         new IndexDefinition(
                                             "users_pkey",
                                                 ["id"],
                                             true,
                                             true,
                                             null,
                                             null)
                                     ],
                                 ForeignKeys: [],
                                 CheckConstraints: [],
                                 UniqueConstraints: [],
                                 Comment: null,
                                 IsPartitioned: false,
                                 PartitionStrategy: null,
                                 ParentTable: null)
                         };

        var result = _generator.GenerateCreateTables(tables);

        result.Should().HaveCount(1);
        result[0].Should().Contain("CREATE TABLE IF NOT EXISTS public.users");
        result[0].Should().Contain("id integer");
        result[0].Should().Contain("NOT NULL");
        result[0].Should().Contain("CONSTRAINT users_pkey PRIMARY KEY (id)");
    }

    [Fact]
    public void GenerateCreateTableStatements_Inheritance_ParentEmittedBeforeChild()
    {
        var parent = new TableDefinition(
            SchemaName: "public",
            Name: "parent_t",
            Columns:
                [
                    new ColumnDefinition("id", "integer", 1, false, null, false, false, null, null)
                ],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: null);

        var child = new TableDefinition(
            SchemaName: "public",
            Name: "child_t",
            Columns:
                [
                    new ColumnDefinition("extra", "text", 1, true, null, false, false, null, null)
                ],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: "public.parent_t",
            PartitionBound: null);

        // Child listed first — parent must still be emitted before it.
        var result = _generator.GenerateCreateTableStatements([child, parent]);

        result.Should().HaveCount(2);
        result[0].TableName.Should().Be("public.parent_t");
        result[1].TableName.Should().Be("public.child_t");
    }

    [Fact]
    public void GenerateCreateTableStatements_InheritanceChild_EmitsInheritsClause()
    {
        // Legacy INHERITS child: has a ParentTable but no PartitionBound.
        // Only columns local to the child (IsLocal) should be declared; inherited
        // columns come from the parent.
        var child = new TableDefinition(
            SchemaName: "public",
            Name: "child_t",
            Columns:
                [
                    new ColumnDefinition(
                        "id",
                        "integer",
                        1,
                        false,
                        null,
                        false,
                        false,
                        null,
                        null,
                        IsLocal: false),
                    new ColumnDefinition(
                        "extra",
                        "text",
                        2,
                        true,
                        null,
                        false,
                        false,
                        null,
                        null,
                        IsLocal: true)
                ],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: "public.parent_t",
            PartitionBound: null);

        var result = _generator.GenerateCreateTableStatements([child]);

        result.Should().HaveCount(1);
        result[0].Sql.Should().Contain("CREATE TABLE IF NOT EXISTS public.child_t");
        result[0].Sql.Should().Contain("INHERITS (public.parent_t)");
        result[0].Sql.Should().Contain("extra text");
        result[0].Sql.Should().NotContain("id integer"); // inherited column not redeclared
        result[0].Sql.Should().NotContain("PARTITION OF");
    }

    [Fact]
    public void GenerateCreateTableStatements_Partition_StillEmitsPartitionOf()
    {
        // Regression: partitioned hierarchy must keep using PARTITION OF, not INHERITS.
        var parent = new TableDefinition(
            SchemaName: "public",
            Name: "logs",
            Columns:
                [
                    new ColumnDefinition("id", "integer", 1, false, null, false, false, null, null)
                ],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: true,
            PartitionStrategy: "RANGE (created_at)",
            ParentTable: null);

        var partition = new TableDefinition(
            SchemaName: "public",
            Name: "logs_2024",
            Columns: [],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: "public.logs",
            PartitionBound: "FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')");

        var result = _generator.GenerateCreateTableStatements([partition, parent]);

        result.Should().HaveCount(2);
        result[0].TableName.Should().Be("public.logs");
        result[1].TableName.Should().Be("public.logs_2024");
        result[1].Sql.Should().Contain("PARTITION OF public.logs");
        result[1].Sql.Should().Contain("FOR VALUES FROM ('2024-01-01') TO ('2025-01-01')");
        result[1].Sql.Should().NotContain("INHERITS");
    }

    [Fact]
    public void GenerateCreateTableStatements_RegularTable_NoInheritsOrPartition()
    {
        var table = new TableDefinition(
            SchemaName: "public",
            Name: "plain",
            Columns:
                [
                    new ColumnDefinition("id", "integer", 1, false, null, false, false, null, null)
                ],
            Indexes: [],
            ForeignKeys: [],
            CheckConstraints: [],
            UniqueConstraints: [],
            Comment: null,
            IsPartitioned: false,
            PartitionStrategy: null,
            ParentTable: null);

        var result = _generator.GenerateCreateTableStatements([table]);

        result.Should().HaveCount(1);
        result[0].Sql.Should().Contain("CREATE TABLE IF NOT EXISTS public.plain");
        result[0].Sql.Should().Contain("id integer");
        result[0].Sql.Should().NotContain("INHERITS");
        result[0].Sql.Should().NotContain("PARTITION OF");
    }

    [Fact]
    public void GenerateForeignKeys_GeneratesAlterTable()
    {
        var fks = new List<ForeignKeyDefinition>
                      {
                          new(
                              "fk_user_role",
                                  ["role_id"],
                              "public",
                              "roles",
                                  ["id"],
                              "NO ACTION",
                              "CASCADE",
                              false,
                              false)
                      };

        var result = _generator.GenerateForeignKeys(fks, "public", "users");

        result.Should().HaveCount(1);
        result[0].Should().Contain("ALTER TABLE");
        result[0].Should().Contain("FOREIGN KEY");
        result[0].Should().Contain("ON DELETE CASCADE");
    }

    [Fact]
    public void GenerateSetSequenceValue_GeneratesSetval()
    {
        var result = _generator.GenerateSetSequenceValue("public", "users_id_seq", 100);

        result.Should().Contain("setval");
        result.Should().Contain("100");
    }

    [Fact]
    public void GenerateSetSequenceValue_MixedCaseName_QuotesIdentifierInsideLiteral()
    {
        // setval parses its text argument like an unquoted identifier (case-folded),
        // so the name must be quoted inside the literal to survive.
        var result = _generator.GenerateSetSequenceValue("sel_test", "MixedCase_Id_seq", 4);

        result.Should().Contain("setval('sel_test.\"MixedCase_Id_seq\"', 4, true)");
    }
}
