# Architecture Rules

## Layer Dependency Direction (strict one-way)

```
DbClone.PostgreSql  →  DbClone.Application  ←  DbClone.UI
                              (interfaces only)
```

- **DbClone.Application** — interfaces, DTOs, models, and shared provider-agnostic orchestration (e.g. `CopyPipeline`). NO provider-specific implementations. Only `Microsoft.Extensions.Logging.Abstractions` as NuGet dependency.
- **DbClone.PostgreSql** — implements Application interfaces with PostgreSQL-specific code. References Application. Registers pipeline stages.
- **DbClone.UI** — references Application (interfaces/DTOs) and PostgreSql **only for DI registration at the composition root** (`App.xaml.cs`). Must NOT use PostgreSql types in any other file.

## Composition Root Exception

`App.xaml.cs` is the composition root and is the **only** file in the UI project allowed to:
- `using DbClone.PostgreSql;`
- Call `services.AddPostgreSqlProvider()`

No other `.cs` file in DbClone.UI may reference the PostgreSql namespace or types.

## Forbidden in DbClone.UI (except App.xaml.cs)

- ❌ `using Npgsql;` — never import
- ❌ `new NpgsqlConnection(...)` — never open a database connection
- ❌ `new NpgsqlConnectionStringBuilder(...)` — never parse/build connection strings directly
- ❌ Any SQL string (`SELECT`, `CREATE`, `DROP`, `INSERT`, `UPDATE`, `DELETE`, `pg_`, `information_schema`)
- ❌ `using DbClone.PostgreSql;` — only in App.xaml.cs
- ❌ `new PgSqlExecutor(...)`, `new PgMetadataReader(...)` — never instantiate provider types
- ❌ `PackageReference` to `Npgsql` — remove if present; Npgsql arrives transitively through PostgreSql

## Forbidden in DbClone.Application

- ❌ Provider-specific implementations (no PostgreSQL, MySQL, etc. — those go in provider projects)
- ❌ Any NuGet package beyond `Microsoft.Extensions.Logging.Abstractions`
- ❌ `using Npgsql;` or any provider-specific import
- ✅ Shared orchestration code that all providers use (e.g. `CopyPipeline`) is allowed

## Forbidden in DbClone.PostgreSql

- ❌ References to DbClone.UI (circular dependency)

## How to add a new database provider

1. Create `DbClone.MySql` project referencing `DbClone.Application`
2. Implement `ITableInfoProvider`, `IDatabaseMaintenanceProvider`, `ITableComparerProvider`, `IConnectionStringService`
3. Add a static `AddMySqlProvider(this IServiceCollection)` extension method
4. In `App.xaml.cs`, call `services.AddMySqlProvider()` — no other UI changes

## Connection string handling

- `ConnectionViewModel` uses `IConnectionStringService` (injected) — never constructs Npgsql types
- URI format (`postgres://...`) is parsed with `System.Uri` (BCL, allowed)
- Key-value format (`Host=...;Port=...`) is parsed via `IConnectionStringService.TryParseKeyValue()`
- All `ConnectionViewModel` → `ConnectionInfo` mapping goes through `ConnectionInfoFactory.FromViewModel()` (single source of truth)

## Security — test data rules (MANDATORY)

- ❌ NEVER use real hostnames, usernames, passwords, database names, or project references in tests or documentation
- ❌ NEVER connect to a live database in unit/integration tests — tests must be hermetic
- ❌ NEVER copy a user's actual connection string (even partially) into code
- ✅ Use obviously fake values: `test.example.com`, `test_user`, `testdb`, `fakeprojectref`
- ✅ When testing special characters in passwords, cover a diverse set (`#`, `!`, `@`, `%`, `&`, spaces) — never mirror the structure of a real credential
- ✅ Supabase/Aiven/Neon project refs in tests must be generic placeholders (e.g. `testrefxyz`, `fakeprojectref`)

## Settings persistence

- `UserSettings` is a plain DTO — no Load/Save logic
- All file I/O for settings is in `SettingsService` (implements `ISettingsService`)

## CHANGELOG maintenance (release workflow)

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format and feeds directly into the auto-update system.

### Format rules

- Top-level heading: `# Changelog`
- Each release is a level-2 heading: `## [X.Y.Z] - YYYY-MM-DD`
- Work-in-progress lives under: `## [Unreleased]`
- Change categories (level-3 headings): `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`, `Security`
- Bottom of the file has reference links: `[X.Y.Z]: https://github.com/AlexNek/DbClone/releases/tag/vX.Y.Z`

### Algorithm: when making code changes

1. **Every user-visible change** (feature, fix, breaking change) — add a bullet under `## [Unreleased]` in the appropriate category (`Added`, `Fixed`, `Changed`, etc.)
2. Keep bullets concise (one line) but descriptive enough for end users
3. Do NOT modify existing versioned sections (`## [1.0.0]`, etc.) unless explicitly asked to correct an error

### Algorithm: preparing a release (when asked to tag/release version X.Y.Z)

1. Rename `## [Unreleased]` → `## [X.Y.Z] - YYYY-MM-DD` (use actual date)
2. Add a new empty `## [Unreleased]` section above it
3. Update the reference links at the bottom:
   - Change `[Unreleased]` link to compare `vX.Y.Z...HEAD`
   - Add `[X.Y.Z]: https://github.com/AlexNek/DbClone/releases/tag/vX.Y.Z`
4. The release workflow (`.github/workflows/release.yml`) will automatically:
   - Extract the `## [X.Y.Z]` section from CHANGELOG.md
   - Write it as the GitHub Release body (`release-notes.md`)
   - The `softprops/action-gh-release` action publishes it with `body_path`
5. The auto-updater (`UpdateService`) uses the release's `html_url` as the changelog link — users clicking "what's new" see the GitHub Release page with the extracted notes

### Connection to auto-update

- `UpdateService` fetches `/releases/latest` JSON from GitHub API
- The JSON `html_url` field points to the Release page (which now contains the CHANGELOG section)
- The release also has `generate_release_notes: true` so GitHub appends auto-generated commit notes below the curated changelog
- Both JSON format (GitHub API) and XML format (AutoUpdater.NET `<item>`) are supported for local testing — see `HelpLocal/testing-autoupdate.md`

### Important

- CHANGELOG.md must exist in the repo root (the release workflow reads it at checkout path)
- If no matching version section is found, the workflow falls back to `[Unreleased]`; if that's empty too, it links to CHANGELOG.md on GitHub

## Edit discipline (MANDATORY)

- Make surgical edits only: change exactly what the task requires.
- Never refactor, "simplify", or restructure adjacent working code unless explicitly asked.
- If a change seems to require touching unrelated structure, stop and ask first.
