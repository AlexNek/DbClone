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
