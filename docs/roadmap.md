# Possible Future Features

This page collects ideas for potential future enhancements — **nothing here is scheduled**. Every feature is implemented only when a user actually needs it. The list exists so that when a requirement comes up, the design space is already visible.

!!! note "User-driven development"
    DbClone evolves based on user requirements, not a predefined roadmap. If you need a feature from this page, open an issue or request it — that's what moves it from "possible" to "implemented".

## Priority Legend

| Tag | Meaning |
|-----|---------|
| 💡 **Backlog** | Useful idea — will be implemented when a user needs it |
| 🔬 **Exploratory** | Open question — feasibility or design not yet settled |

---

## Copy & Data Transfer

| Feature | Priority | Description |
|---------|----------|-------------|
| **Schema-level filtering** | 💡 Backlog | Select entire schemas to include/exclude without toggling the global "Platform Schemas" flag. |
| **Parallel data copy** | 🔬 Exploratory | Copy multiple tables in parallel using concurrent `COPY` streams to improve throughput on high-bandwidth connections. |
| **Incremental / CDC sync** | 🔬 Exploratory | Detect changed rows via `xmin`/`max_age` or logical replication slots and transfer only deltas instead of full tables. |
| **Copy specific queries** | 💡 Backlog | Allow the user to supply a `SELECT` query per table to copy a filtered subset of rows (e.g. `WHERE created_at > ...`). |
| **Copy direction reversal** | 💡 Backlog | Swap source and destination in one click to copy data back (useful for seeding a development database from production). |
| **Compression during transfer** | 🔬 Exploratory | Compress data in-flight (e.g. LZ4 or zstd over the wire) to reduce bandwidth for large copies over slow links. |
| **Batch size auto-tuning** | 💡 Backlog | Automatically adjust the COPY batch size based on observed throughput and available memory. |

---

## Comparison

| Feature | Priority | Description |
|---------|----------|-------------|
| **Side-by-side data diff viewer** | 💡 Backlog | Show actual row-level differences in a split-pane viewer after a comparison run. |
| **Historical comparison snapshots** | 💡 Backlog | Save comparison results over time and diff them to track when drift was introduced. |
| **Custom comparison rules** | 🔬 Exploratory | Let users define ignore rules (e.g. ignore `updated_at` columns, ignore specific constraints, treat certain enum orderings as equivalent). |
| **Schema-only comparison mode** | 💡 Backlog | Compare DDL structure without row counts or checksums for a fast structural diff. |

---

## Connections & Security

| Feature | Priority | Description |
|---------|----------|-------------|
| **SSH tunnel support** | 💡 Backlog | Built-in SSH tunnel configuration so users don't need external tools (PuTTY, OpenSSH) to connect through bastion hosts. |
| **Connection templates** | 💡 Backlog | Save reusable connection templates (e.g. "Supabase EU" with pre-filled port, SSL, and common settings). |
| **Connection health monitoring** | 💡 Backlog | Periodic background ping of saved connections with visual indicator (green/red dot) showing availability. |
| **Secrets manager integration** | 🔬 Exploratory | Store credentials in Windows Credential Manager or Azure Key Vault instead of DPAPI-encrypted JSON. |
| **Read-only connection mode** | 💡 Backlog | Mark a connection as read-only to prevent accidental writes — DbClone will refuse any destructive operation on that connection. |
| **Multi-user connection sharing** | 🔬 Exploratory | Share encrypted connection profiles across team members via a central server or shared file. |

---

## Copy Modes & Workflows

| Feature | Priority | Description |
|---------|----------|-------------|
| **Scheduled / recurring copies** | 💡 Backlog | Run copies on a cron-like schedule (e.g. nightly staging refresh) with email or webhook notification on completion. |
| **Copy profiles / presets** | 💡 Backlog | Save a named set of copy options (mode, object toggles, schema filters, table filters) and reuse them across sessions. |
| **Dry-run mode** | 💡 Backlog | Execute the full pipeline without writing anything — report what *would* be created, copied, or dropped. |
| **Copy from SQL dump file** | 🔬 Exploratory | Parse a `pg_dump` SQL file as a source and replay it to a destination, without needing a live source connection. |
| **Reverse engineering (destination → model)** | 🔬 Exploratory | Read the destination schema and generate a migration script to bring it in line with the source. |

---

## Platform & Provider Support

| Feature | Priority | Description |
|---------|----------|-------------|
| **Additional platform definitions** | 💡 Backlog | Add `.platform` files for Amazon RDS, Azure Database for PostgreSQL, Google Cloud SQL, and Crunchy Bridge with their specific extensions and schemas. |
| **MySQL provider** | 🔬 Exploratory | Implement `DbClone.MySql` with `ITableInfoProvider`, `IDatabaseMaintenanceProvider`, etc. Architecture supports it via the provider interface layer. |
| **SQL Server provider** | 🔬 Exploratory | Implement `DbClone.SqlServer` following the same provider pattern. |
| **Cross-platform copy** | 🔬 Exploratory | Copy between different database engines (e.g. PostgreSQL → MySQL) via a **Canonical Relational Database Model (CRDM)** — each provider imports/exports to a shared intermediate format, avoiding N×N converter explosion. |
| **Cross-OS support** | 🔬 Exploratory | Evaluate Avalonia or MAUI as an alternative UI framework to enable macOS and Linux builds. |

---

## UI & Usability

| Feature | Priority | Description |
|---------|----------|-------------|
| **Copy progress chart** | 💡 Backlog | Visualise per-table copy progress as a treemap or flame chart — area proportional to row count, colour indicating status. |
| **Quick-assign from Connection Manager** | 💡 Backlog | Right-click a connection in the Connection Manager and choose "Set as Source" or "Set as Destination" to assign it on the main window without manually selecting from the combo box. |
| **Recent operations panel** | 💡 Backlog | Show a list of recent copy/compare runs with their settings, duration, and result — one click to re-run. |
| **Command-line interface (CLI)** | 💡 Backlog | Headless `dbclone.exe copy --source ... --dest ... --mode full` for scripting, CI pipelines, and automation. |
| **Notification on completion** | 💡 Backlog | Windows toast notification (and optional webhook / email) when a long-running copy or comparison finishes. |
| **Dark mode for comparison results** | 💡 Backlog | Ensure the HTML comparison report respects a dark theme or adapts to system preference. |
| **Accessibility improvements** | 💡 Backlog | Full keyboard navigation, screen-reader support, and high-contrast theme compliance across all views. |
| ** Localisation / i18n** | 🔬 Exploratory | Extract all UI strings into resource files for translation into other languages. |

---

## Reporting & Logging

| Feature | Priority | Description |
|---------|----------|-------------|
| **Copy operation audit log** | 💡 Backlog | Persistent, searchable log of every copy operation with timestamps, settings, and results — exportable for compliance. |
| **Real-time log streaming** | 💡 Backlog | Stream the log to an external endpoint (file, syslog, Seq, ELK) during execution for centralised monitoring. |
| **Diff report in comparison export** | 💡 Backlog | Include the actual DDL diff (not just "Different") in exported comparison reports. |
| **Performance bottleneck report** | 🔬 Exploratory | Post-copy analysis highlighting the slowest stages, largest tables, and throughput bottlenecks with recommendations. |

---

## Pipeline & Performance

| Feature | Priority | Description |
|---------|----------|-------------|
| **Pause and resume mid-stage** | 💡 Backlog | Currently pause/resume works between stages. Allow pausing *during* a long data copy and resuming from the exact row. |
| **Configurable worker pool** | 🔬 Exploratory | Let the user set the number of concurrent workers for data copy and DDL stages. |
| **Streaming replication integration** | 🔬 Exploratory | Optionally use logical replication for the initial data sync, falling back to COPY for unsupported setups. |
| **Memory-mapped COPY** | 🔬 Exploratory | Use memory-mapped buffers for very large tables to reduce GC pressure during data transfer. |

---

## Already Implemented

For reference, these features from earlier roadmaps are **already shipped** in the current release:

- Four copy modes (Full, Resume, Update, Backup)
- Manual table selection with named presets — choose which tables Copy, Compare, and Backup process, with FK relationship explorer and validation summary
- Platform auto-detection via `.platform` files (Supabase, Aiven, Neon, Vanilla)
- Full database comparison with structural DDL diff
- Dependency-ordered pipeline with 21 stages
- Connection import/export with AES-256 encryption
- Connection groups and color coding
- Real-time progress with per-table ETA
- Object count validation post-copy
- Light/Dark/System theme support
- Keyboard shortcuts for all major operations
- Comparison report export (HTML, Markdown, JSON, Plain Text)
- Row-level and checksum verification modes
- Connection heartbeat between stages
- Automatic extension-owned object exclusion
