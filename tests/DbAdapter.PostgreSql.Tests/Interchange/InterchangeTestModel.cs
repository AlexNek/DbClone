using CanonicalModel.Enums;
using CanonicalModel.Models;

namespace DbAdapter.PostgreSql.Tests;

/// <summary>
/// In-memory database models for the interchange mapping tests.
/// <para>
/// <see cref="Full"/> covers every object kind the mapper reads, so a test can state the one
/// property it cares about instead of rebuilding a database for each assertion. The names are the
/// obvious placeholders the repository's test-data rule asks for, and nothing here opens a
/// connection: the mapper and the identity graph are pure functions over these records.
/// </para>
/// </summary>
internal static class InterchangeTestModel
{
    /// <summary>An empty model, for the tests that need exactly one object kind.</summary>
    internal static DatabaseModel Empty() =>
        new(
            DatabaseName: "testdb",
            ServerVersion: "PostgreSQL 16.0",
            Schemas: [],
            Tables: [],
            Views: [],
            MaterializedViews: [],
            Sequences: [],
            Enums: [],
            Domains: [],
            CompositeTypes: [],
            Functions: [],
            Triggers: [],
            Policies: [],
            Publications: [],
            Subscriptions: [],
            Extensions: []);

    /// <summary>
    /// The model shared by the mapping tests: two schemas, three tables (one with no key at all),
    /// an enum column, a generated column, a foreign key, a view that reads a relation the
    /// catalog does not contain, a procedure, a disabled statement trigger, and one object of
    /// every remaining kind.
    /// </summary>
    internal static DatabaseModel Full() =>
        Empty() with
        {
            Schemas =
            [
                new SchemaDefinition("reporting", "test_user"),
                new SchemaDefinition("public", "test_user"),
            ],
            Enums =
            [
                new EnumDefinition("public", "mood", ["sad", "happy"], null),
            ],
            Tables =
            [
                Customers(),
                Orders(),
                Keyless(),
            ],
            Views =
            [
                // "public.ghost" is deliberately not an object of this model: a dependency on
                // something the catalog does not hold must be skipped, not invented.
                new ViewDefinition(
                    "public",
                    "v_active_customers",
                    "SELECT id, name FROM public.customers",
                    null,
                    ["public.customers", "public.ghost"]),
            ],
            MaterializedViews =
            [
                new MaterializedViewDefinition(
                    "reporting",
                    "mv_totals",
                    "SELECT customer_id, sum(total) FROM public.orders GROUP BY 1",
                    null,
                    ["customer_id", "grand_total"],
                    null,
                    ["public.orders"]),
            ],
            Functions =
            [
                new FunctionDefinition(
                    "public",
                    "fn_notify",
                    "plpgsql",
                    "trigger",
                    "CREATE FUNCTION public.fn_notify() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;",
                    [],
                    EFunctionVolatility.Volatile,
                    false,
                    false,
                    null,
                    false),
                new FunctionDefinition(
                    "public",
                    "add_customer",
                    "plpgsql",
                    "integer",
                    "CREATE FUNCTION public.add_customer(name character varying) RETURNS integer LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;",
                    [new FunctionParameter("name", "character varying", EParameterMode.In, null)],
                    EFunctionVolatility.Volatile,
                    false,
                    false,
                    null,
                    false),
                new FunctionDefinition(
                    "public",
                    "run_cleanup",
                    "plpgsql",
                    "void",
                    "CREATE PROCEDURE public.run_cleanup() LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;",
                    [],
                    EFunctionVolatility.Volatile,
                    false,
                    false,
                    null,
                    true),
            ],
            Triggers =
            [
                new TriggerDefinition(
                    "public",
                    "tr_audit",
                    "customers",
                    "BEFORE",
                    ["INSERT", "UPDATE"],
                    "public",
                    "fn_notify",
                    true,
                    true,
                    "NEW.id IS NOT NULL",
                    null,
                    // What pg_get_triggerdef reports for this trigger: the only record of its WHEN
                    // clause and of the order its events were declared in.
                    Definition: "CREATE TRIGGER tr_audit BEFORE INSERT OR UPDATE ON public.customers "
                        + "FOR EACH ROW WHEN (NEW.id IS NOT NULL) EXECUTE FUNCTION public.fn_notify()"),
                new TriggerDefinition(
                    "public",
                    "tr_disabled",
                    "orders",
                    "AFTER",
                    ["DELETE"],
                    "public",
                    "fn_notify",
                    false,
                    false,
                    null,
                    null),
            ],
            Policies =
            [
                new PolicyDefinition(
                    "public",
                    "customers_read",
                    "customers",
                    "SELECT",
                    true,
                    ["test_user"],
                    "true",
                    null),
            ],
            Sequences =
            [
                new SequenceDefinition(
                    "public",
                    "order_seq",
                    1,
                    1,
                    null,
                    null,
                    1,
                    false,
                    "bigint",
                    null),
            ],
            Extensions =
            [
                new ExtensionDefinition("pgcrypto", "ext", "1.3", null),
            ],
            Publications =
            [
                new PublicationDefinition("pub_all", true, [], null),
            ],
            Subscriptions =
            [
                new SubscriptionDefinition(
                    "sub_1",
                    "Host=test.example.com;Database=testdb;Username=test_user;Password=fake_pw_123",
                    "pub_all",
                    true,
                    null),
            ],
            Domains =
            [
                // pg_get_constraintdef reports a domain's check as the whole CHECK clause, which is the
                // shape the capture writes into the statement it assembles.
                new DomainDefinition(
                    "public",
                    "positive_amount",
                    "numeric",
                    null,
                    "CHECK ((VALUE > 0))",
                    false,
                    null),
            ],
            CompositeTypes =
            [
                new CompositeTypeDefinition(
                    "public",
                    "address",
                    [new ColumnDefinition("city", "text", 1, true, null, false, false, null, null)],
                    null),
            ],
        };

    /// <summary>A table with no key of any kind, which cannot promise a repeatable row order.</summary>
    internal static TableDefinition Keyless() =>
        new(
            "reporting",
            "key_value",
            [new ColumnDefinition("payload", "text", 1, true, null, false, false, null, null)],
            [],
            [],
            [],
            [],
            null,
            false,
            null,
            null);

    /// <summary>The referenced parent table: primary key, checks, unique, generated and enum columns.</summary>
    private static TableDefinition Customers() =>
        new(
            "public",
            "customers",
            [
                new ColumnDefinition("id", "integer", 1, false, null, true, false, null, "primary key"),
                new ColumnDefinition("name", "character varying(80)", 2, false, null, false, false, null, null),
                new ColumnDefinition("balance", "numeric(10,2)", 3, true, "2", false, false, null, null),
                new ColumnDefinition("status", "mood", 4, false, "'happy'", false, false, null, null),
                new ColumnDefinition("created_at", "timestamp with time zone", 5, false, "now()", false, false, null, null),
                new ColumnDefinition("total", "integer", 6, true, null, false, true, "(id * 2)", null),
            ],
            [
                new IndexDefinition(
                    "pk_customers",
                    ["id"],
                    IsUnique: true,
                    IsPrimary: true,
                    FilterExpression: null,
                    Tablespace: null,
                    Definition: "CREATE UNIQUE INDEX pk_customers ON public.customers USING btree (id)"),
                new IndexDefinition(
                    "ix_customers_name",
                    ["name"],
                    IsUnique: false,
                    IsPrimary: false,
                    FilterExpression: null,
                    Tablespace: null,
                    Definition: null),
            ],
            [],
            [new CheckConstraintDefinition("chk_balance", "balance >= 0::numeric", false, false)],
            [new UniqueConstraintDefinition("uq_customers_name", ["name"], false, false)],
            "customer master data",
            false,
            null,
            null);

    /// <summary>The child table, holding the only foreign key in the fixture.</summary>
    private static TableDefinition Orders() =>
        new(
            "public",
            "orders",
            [
                new ColumnDefinition("id", "bigint", 1, false, null, false, false, null, null),
                new ColumnDefinition("customer_id", "integer", 2, false, null, false, false, null, null),
            ],
            [
                new IndexDefinition(
                    "pk_orders",
                    ["id"],
                    IsUnique: true,
                    IsPrimary: true,
                    FilterExpression: null,
                    Tablespace: null,
                    Definition: "CREATE UNIQUE INDEX pk_orders ON public.orders USING btree (id)"),
            ],
            [
                new ForeignKeyDefinition(
                    "fk_orders_customer",
                    ["customer_id"],
                    "public",
                    "customers",
                    ["id"],
                    "NO ACTION",
                    "CASCADE",
                    false,
                    false),
            ],
            [],
            [],
            null,
            false,
            null,
            null);
}
