# Pipeline Stages

DbClone executes a pipeline of ordered stages. Each stage is independent — a failure in one non-critical stage doesn't stop the others.

## Stage Order

| # | Stage | What it does |
|---|-------|-------------|
| 1 | **Connect** | Opens connections to source and destination; resolves platform definitions (`.platform` files) for both hosts |
| 2 | **DetectCapabilities** | Probes PostgreSQL versions, extensions, and platform features |
| 3 | **ReadMetadata** | Reads the full source schema (tables, columns, types, functions, etc.) |
| 4 | **AnalyzeDependencies** | Builds a dependency graph and computes creation order |
| 5 | **CreateSchemas** | Creates schemas; detects and repairs missing system schemas (e.g. `information_schema`); excludes non-writable platform schemas |
| 6 | **CreateExtensions** | Installs required extensions (`uuid-ossp`, `pgcrypto`, etc.) |
| 7 | **CreateSequences** | Creates standalone and serial sequences (identity sequences are skipped — created implicitly by table DDL) |
| 8 | **CreateTypes** | Creates enums, domains, and composite types |
| 9 | **CreateFunctions** | Creates functions and procedures (first pass — some may fail if they reference tables) |
| 10 | **CreateTables** | Creates tables without foreign keys; detects tables blocked by unavailable extensions |
| 11 | **ReconcileColumns** | Reconciles column differences between source and destination tables |
| 12 | **CopyData** | Binary COPY of all table data with per-table progress |
| 13 | **CreateIndexes** | Creates secondary (non-primary-key) indexes after data copy for optimal performance |
| 14 | **CreateConstraints** | Adds foreign keys, unique constraints, and check constraints |
| 15 | **SyncSequences** | Sets sequence values to match source (after data is copied); resolves owned sequences via `pg_get_serial_sequence()` |
| 16 | **RetryFunctions** | Retries functions that failed in first pass (now that tables exist) |
| 17 | **CreateViews** | Creates views (dependency-ordered with retry) and materialized views |
| 18 | **CreateTriggers** | Creates triggers |
| 19 | **Validate** | Two-level verification: data validation (row counts, checksums, or full content) plus object count validation (tables, indexes, views, sequences, functions, triggers) |
| 20 | **ReCopyMismatched** | Re-copies any tables that failed validation |

## Critical vs Non-Critical Stages

**Critical stages** (Connect, DetectCapabilities, ReadMetadata, CopyData): if they fail, the pipeline aborts immediately.

- Connect
- DetectCapabilities
- ReadMetadata
- CopyData

**Non-critical stages**: if they fail, the pipeline continues and reports the error at the end.

- CreateViews, CreateTriggers, CreateFunctions, CreateIndexes, etc.

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
