# DbClone

A Windows desktop application for cloning and migrating PostgreSQL databases — including schema, data, and extension-aware DDL generation.

Built for moving databases off managed platforms (Supabase, Aiven, Neon, etc.) to vanilla PostgreSQL without losing schema fidelity.

## Why DbClone?

You might ask: *"Why not just use `pg_dump` / `pg_restore` / DBeaver / pgAdmin?"*

Those tools work great for vanilla PostgreSQL-to-PostgreSQL copies. But the moment your **source** is a managed platform like Supabase, things break:

| Problem | What happens with standard tools | How DbClone handles it |
|---------|----------------------------------|------------------------|
| Extension-owned objects | `pg_dump` emits `CREATE FUNCTION` for functions owned by `pg_stat_statements`, `supabase_vault`, etc. — restore fails | Queries `pg_depend` to identify extension-owned objects and **skips them entirely** |
| `search_path` assumptions | Functions reference `uuid_generate_v4()` without schema qualification — fails on target | Injects runtime `SET search_path` so unqualified calls resolve correctly |
| Managed schemas (`auth`, `storage`, `realtime`) | Attempts to recreate Supabase-internal schemas — permission denied | **Platform definition files** (`.platform`) auto-detect the host, resolve version-specific schemas and extensions for Supabase, Aiven, Neon, and vanilla PostgreSQL |
| Dependency ordering | Objects created in wrong order → cascading failures | Topological sort with cycle detection across types, functions, tables, views |
| Interrupted copy | Start over from scratch | **Resume mode** — compares row counts, copies only missing/mismatched tables |
| No visibility | CLI output, no progress | Real-time per-table progress, ETA, row counts, live log panel |
| Silent schema failures | Objects silently missing on destination | **Object count validation** — verifies tables, indexes, views, sequences, functions, and triggers after copy |

**In short:** DbClone is purpose-built for the *messy* real-world case where your source database has extensions, managed schemas, and platform-specific objects that standard tools choke on.

## How to Use

### 1. Set up connections

Open the **Connection Manager** and add your source and destination databases. You can paste a connection URI (`postgres://user:pass@host:5432/db`) or fill in individual fields. Connections are saved locally and can be color-coded for quick identification.

### 2. Choose a copy mode

| Mode | When to use |
|------|-------------|
| **Full** | Fresh clone to an empty database — drops and recreates everything |
| **Resume** | A previous copy was interrupted (network drop, timeout) — skips DDL, copies only missing data |
| **Update** | Destination already exists and you want to sync changed tables |
| **Backup** | Creates a copy in a new auto-named database with customizable name |

### 3. Configure options (optional)

Toggle what to include: data, indexes, constraints, functions, triggers, views, materialized views, sequences, RLS policies, comments, and platform schemas. The **Connection Type** dropdown auto-detects your platform (Supabase, Aiven, Neon, or vanilla PostgreSQL) from the hostname and pre-fills the correct port and SSL mode. Adjust batch size and parallelism for large databases.

### 4. Run the copy

Hit **Start**. The pipeline executes in order:

```
Connect → DetectCapabilities → ReadMetadata → AnalyzeDependencies
→ CreateSchemas → CreateExtensions → CreateSequences → CreateTypes
→ CreateFunctions → CreateTables → ReconcileColumns → CopyData
→ CreateIndexes → CreateConstraints → SyncSequences → RetryFunctions
→ CreateViews → CreateTriggers → Validate → ReCopyMismatched
```

> **Note:** `CreateFunctions` runs before `CreateTables` (first pass); failed functions are retried after tables exist via `RetryFunctions`. `CreateIndexes` runs separately after data copy for optimal performance.

You'll see real-time progress per table, an ETA, and a live log. If a single table fails, the rest continue — errors are isolated and reported at the end.

### 5. Verify

After the copy, the built-in **validation stage** performs two levels of verification:

1. **Data validation** — compares row counts, checksums, or full content between source and destination (configurable verify mode). Mismatched tables are automatically re-copied.
2. **Object count validation** — verifies that the expected number of tables, indexes, views, materialized views, sequences, functions, and triggers exist on the destination, catching silent omissions.

## Features

- **Full schema cloning** — tables, views, functions, triggers, sequences, enums, domains, composite types, constraints, RLS policies
- **Platform-aware** — `.platform` definition files auto-detect Supabase, Aiven, Neon, and vanilla PostgreSQL; resolve version-specific schemas, extensions, and connection defaults
- **Extension-aware** — detects extension-owned objects via `pg_depend` and skips re-creating them; adjusts `search_path` at runtime
- **Dependency-ordered DDL** — topological sort with cycle detection ensures objects are created in the correct order
- **Data copy with progress** — row-based progress reporting, ETA calculation, per-table error isolation
- **Resume & Update modes** — pick up where you left off or sync incremental changes
- **Enhanced validation** — data verification (row count, checksum, full) plus object count validation for tables, indexes, views, sequences, functions, and triggers
- **Database comparison** — bidirectional structural comparison with detailed DDL diffing (columns, PKs, FKs, checks, unique constraints, sequences, triggers, views); partition-aware CHECK constraint normalization
- **Connection manager** — save, organize, color-code, import/export connections; platform type dropdown auto-detects host and fills defaults; one-click **Export All / Import All** backup with optional AES-256 encryption
- **Dark theme** — full light/dark theme support
- **Auto-update** — checks GitHub Releases on startup; prompts to download and install when a new version is available
- **Fluent Design UI** — modern WPF interface powered by [WPF-UI](https://github.com/lepoco/wpfui)

## Screenshots

<!-- Add screenshots here -->

## Requirements

- Windows 10/11 (x64)
- Source and target must be PostgreSQL (any version supported by Npgsql)
- No .NET runtime installation needed — the app ships self-contained

## Installation

Download the latest `DbClone-Setup-x.y.z.exe` from [GitHub Releases](https://github.com/user-alex-new/DbClone/releases) and run it.

The installer supports silent updates:

```
DbClone-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## Building from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for installer builds)

### Build & Run

```powershell
dotnet build DbClone.slnx
dotnet run --project src/DbClone.UI
```

### Run Tests

```powershell
dotnet test tests/Application.Tests
dotnet test tests/PostgreSql.Tests
```

### Build Installer

```powershell
dotnet publish src/DbClone.UI/DbClone.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishDir=bin/publish
iscc /DAppVersion=1.0.0 installer\DbClone.iss
```

Or use the provided script:

```powershell
.\build-installer.ps1
```

## Architecture

```
DbClone.PostgreSql  →  DbClone.Application  ←  DbClone.UI
                              (interfaces & DTOs)
```

| Project | Responsibility |
|---------|---------------|
| `DbClone.Application` | Interfaces, DTOs, models, provider-agnostic orchestration (pipeline) |
| `DbClone.PostgreSql` | PostgreSQL-specific implementations (Npgsql, DDL generation, metadata) |
| `DbClone.UI` | WPF desktop app (MVVM, WPF-UI Fluent controls) |

Adding a new database provider only requires implementing the Application interfaces in a new project — no UI changes needed.

## Versioning

Uses [GitVersion 6.x](https://gitversion.net/) with `ContinuousDeployment` mode:

| Branch | Tag |
|--------|-----|
| `main` | *(stable)* |
| `develop` | `alpha` |
| `feature/*` | `beta` |
| `release/*` | `rc` |

Version bumps via commit messages: `+semver: major`, `+semver: minor`, `+semver: patch`.

## CI/CD

- **CI** — builds, tests, and verifies the installer on every push/PR to `main`/`develop`
- **Release** — triggered by `v*` tags; publishes a self-contained build, creates a GitHub Release with the installer attached

Full details in [CI-CD.md](CI-CD.md).

## License

**Compiled binary** — free for any use under the [MIT License](LICENSE.txt) (personal, commercial, redistribution) without restriction.

**Source code** — governed by [PolyForm Shield 1.0.0](https://polyformproject.org/licenses/shield/1.0.0) with an open source exception: free for OSI-approved open source projects (no competing database copy/migration tools); commercial licensing available for proprietary use or competing products. See [LICENSE.txt](LICENSE.txt) for details.
