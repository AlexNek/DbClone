# Pipeline Stages

DbClone executes a pipeline of ordered stages. Each stage is independent — a failure in one non-critical stage doesn't stop the others.

## Stage Order

| # | Stage | What it does |
|---|-------|-------------|
| 1 | **Connect** | Opens connections to source and destination; resolves platform definitions (`.platform` files) for both hosts |
| 2 | **DetectCapabilities** | Probes PostgreSQL versions, extensions, and platform features |
| 3 | **ReadMetadata** | Reads the full source schema (tables, columns, types, functions, etc.) |
| 4 | **ApplyTableFilter** | Applies the active [table selection](../connections/table-selection.md) — removes excluded tables (and their owned objects) from the source model; skipped when "All Tables" is active |
| 5 | **AnalyzeDependencies** | Builds a dependency graph and computes creation order |
| 6 | **CreateSchemas** | Creates schemas; detects and repairs missing system schemas (e.g. `information_schema`); excludes non-writable platform schemas |
| 7 | **CreateExtensions** | Installs required extensions (`uuid-ossp`, `pgcrypto`, etc.) |
| 8 | **CreateSequences** | Creates standalone and serial sequences (identity sequences are skipped — created implicitly by table DDL) |
| 9 | **CreateTypes** | Creates enums, domains, and composite types |
| 10 | **CreateFunctions** | Asks for the `SET` privileges the functions declare, then creates functions and procedures (first pass — some may fail if they reference tables) |
| 11 | **CreateTables** | Creates tables without foreign keys; detects tables blocked by unavailable extensions |
| 12 | **ReconcileColumns** | Reconciles column differences between source and destination tables |
| 13 | **CopyData** | Binary COPY of all table data with per-table progress |
| 14 | **CreateIndexes** | Creates secondary (non-primary-key) indexes after data copy for optimal performance |
| 15 | **CreateConstraints** | Adds unique constraints and check constraints, and the foreign keys whose parent table is inside the copied scope |
| 16 | **SyncSequences** | Sets sequence values to match source (after data is copied); resolves owned sequences via `pg_get_serial_sequence()` |
| 17 | **RetryFunctions** | Retries functions that failed in first pass (now that tables exist) |
| 18 | **CreateViews** | Creates views (dependency-ordered with retry) and materialized views |
| 19 | **CreateTriggers** | Creates triggers |
| 20 | **Validate** | Two-level verification: data validation (row counts, checksums, or full content) plus object count validation (tables, indexes, views, sequences, functions, triggers) |
| 21 | **ReCopyMismatched** | Re-copies any tables that failed validation |

## Foreign Keys and Scope

A foreign key is stored with its child table, but the engine refuses to create it unless the table it
references exists at that moment. The **CreateConstraints** stage therefore creates a constraint only
when its parent table is inside the copied scope — present in the copied model and successfully created:

- **In scope** — the parent is copied too, so the constraint is created as before.
- **Out of scope** — the parent lives in a schema left out of the copy (e.g. a platform schema with
  **Platform Schemas** unchecked), or the parent table failed to be created. The constraint is **never
  sent to the server**: it is not attempted, so it cannot fail, and the copy still ends "complete".

Out-of-scope constraints are reported **once per parent table**, not once per constraint:

```
[OK] CreateConstraints: 53 total: 50 succeeded, 3 skipped
SKIPPED: auth.users: 18 foreign key(s) not created — parent table is outside the copied scope
```

The stage's object count includes created, failed and skipped constraints, so the header bar matches the
summary line. A constraint that *is* in scope and is still refused — copied rows that violate it on the
destination — keeps the usual failure warning and does not stop the copy.

## Functions That Set a Parameter

A function may carry a `SET <parameter>` clause, and the server checks it when the function is created: a
role that may not set that parameter cannot store the clause, so the function is refused with
`42501 permission denied to set parameter`. The **CreateFunctions** stage therefore asks for the privilege
first — once, before the first `CREATE FUNCTION` — for the parameters the copied functions declare in
`pg_proc.proconfig`:

```sql
GRANT SET ON PARAMETER log_min_messages TO <the copy's role>
```

- **Only five logging parameters are ever asked for**: `log_min_messages`, `log_min_error_statement`,
  `log_min_duration_statement`, `log_duration`, `log_statement`. What they change is what the server writes to
  its log; any other parameter could change what a function is allowed to do, so a source database can never
  make the copy grant one.
- **The grant is not revoked.** A function re-applies its setting every time it runs, so revoking the
  privilege afterwards would break the function rather than undo the copy. The five names are the whole
  lasting change, and each grant is one line in the log.
- **A refusal is not a failure.** A role that does not hold the privilege cannot confer it; the stage logs the
  refusal and attempts every function anyway. A function that is still refused then costs that one object and
  is named once at warning severity, together with the statement to hand an administrator.
- **PostgreSQL 15 is the floor.** `GRANT … ON PARAMETER` does not exist before it, so on an older
  **destination** the stage attempts nothing. A function that is then refused keeps the server's own message and
  is given no statement, because no grant exists on such a server to name — running the copy as a superuser is
  the only remedy there.

The stage reports how many parameters it granted as `SET privileges granted` in its summary; the line appears
only when the copied functions declare at least one of the five.

## Critical vs Non-Critical Stages

**Critical stages** (Connect, DetectCapabilities, ReadMetadata, ApplyTableFilter, CopyData): if they fail, the pipeline aborts immediately.

- Connect
- DetectCapabilities
- ReadMetadata
- ApplyTableFilter
- CopyData

**Non-critical stages**: if they fail, the pipeline continues and reports the error at the end.

- CreateViews, CreateTriggers, CreateFunctions, CreateIndexes, etc.

### What ends a run, what only costs an object

A stage's own status and the run's verdict are two different things, because an object the server refuses
is not the same loss as a table's rows.

- **Ends the run** — a critical stage, a table that could not be created, rows that failed to copy or to
  validate, and a non-critical stage that fails as a whole (no destination connection, a refused index,
  sequence or composite type). The run ends **failed** and the errors panel lists them.
- **Costs one object** — a function, a view, a materialized view or a trigger the server refused
  (`RetryFunctions`, `CreateViews`, `CreateTriggers`). Each is named **once**, at **warning** severity,
  with its schema-qualified name and the server's message; the stage itself still counts as done, the
  pipeline continues, and the run ends **complete** with the number of objects that were not created.

So `[OK] CreateViews: 3 objects` means the stage finished its work — not that every view exists. The views
that were not created are named in the warnings, and the status line ends
`Copy complete in 241.1s — 1 object not created (see the log)`. See
[Troubleshooting](../troubleshooting.md#copy-complete-but-some-objects-were-not-created).

## Stage Lifecycle

Each stage reports:

- **Success/Failure** status
- **Duration** (how long it took)
- **Objects processed** (count of items handled)
- **Details** (structured facts rendered into human-readable messages by the UI layer)
- **Error message** (if failed)

All of this is visible in the log panel during execution.

## Connection Heartbeat

Between every stage, DbClone sends a `SELECT 1` ping to both source and destination connections. This prevents cloud proxies (Aiven, Supabase, PgBouncer) from dropping idle TCP connections during long-running pipelines.

## Resume/Update Mode

In Resume and Update modes, DDL stages (CreateSchemas through CreateTriggers) are **skipped**. Only data stages run:

```
Connect → DetectCapabilities → ReadMetadata → CopyData → Validate → ReCopyMismatched
```

The **CreateSchemas** stage still runs a subset of its logic in Resume/Update mode: it checks for missing system schemas (e.g. `information_schema`) and attempts repairs if needed.
