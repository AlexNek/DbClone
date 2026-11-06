# Architecture Rules

## Layer Dependency Direction (strict one-way)

```
DbClone.UI → DbClone.Application → DbAdapter.Definitions → CanonicalModel
                    (interfaces/DTOs)   (roles, shared records,
                                          the execution loop)

DbAdapter.PostgreSql → DbAdapter.Definitions, CanonicalModel
plugins/postgresql   → a publish of DbAdapter.PostgreSql; never a project reference
```

- **DbAdapter.Definitions** — the shared boundary: role interfaces, adapter records (`AdapterInfo`, `DatabaseTarget`, …), `IConnectionStringSyntax`, `IConnectionTextFormat`, `IModelComparer`, and the provider-neutral execution loop (`ICopyStage`, `ICopyPipeline`, `CopyPipeline`, `CopyContext`) plus the platform file reader (`PlatformDefinitionLoader`, `PlatformSchemaResolver` contract, `VersionRangeParser`). Holds **no SQL, no engine name, no driver** — enforced by a test. NuGet dependencies: `Microsoft.Extensions.Logging.Abstractions` only.
- **CanonicalModel** — the model records, enums (including `ESslMode`), and table identity. Names no engine.
- **DbClone.Application** — the plugin catalog (`IAdapterCatalog`/`DatabaseAdapterCatalog`), DTOs, copy workflow, comparison, selection, settings, reports. References Definitions and CanonicalModel only. NO provider-specific implementations. NuGet dependencies: `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`.
- **DbAdapter.PostgreSql** — implements the Definitions roles for PostgreSQL and composes its own engine graph (no DI container). Published into `plugins/postgresql`, never referenced by the UI at compile time.
- **DbClone.UI** — project-references Application and CanonicalModel (Definitions types arrive transitively through Application). Names no engine and never reaches `DbAdapter.PostgreSql`; it may consume the engine-neutral boundary types the design places in `DbAdapter.Definitions` (`IAdapterCatalog`, `IModelComparer`, `ECompareStatus` — see `refactors/09/adapterPluginDesign.md` line 72 and 178).

## Adapters Are Plugins (Composition Root)

An engine adapter is discovered from a `plugins/<dialect>` folder at startup — never constructed by name, registered as a service, or referenced in UI code. `App.xaml.cs` is the composition root and its only engine-related work is to register one `IAdapterCatalog` over `Path.Combine(AppContext.BaseDirectory, "plugins")` and build the connection-text import/export services from `IAdapterCatalog.Formats`. It contains no `using DbAdapter.PostgreSql` and no `AddPostgreSqlProvider()` call. `DbClone.UI.csproj` is the **only** file that names the adapter project, in exactly two lines: a `ReferenceOutputAssembly="false"` project reference (build ordering) and the `PublishAdapters` target that publishes it into `plugins/postgresql`.

Selection is by the connection's dialect: `DatabaseTargetFactory` resolves the adapter through the catalog (`null` engine → the single installed adapter; an unknown engine → a reported error, never a silent fallback).

## Forbidden in DbClone.UI

- ❌ `using Npgsql;` — never import
- ❌ `new NpgsqlConnection(...)` — never open a database connection
- ❌ `new NpgsqlConnectionStringBuilder(...)` — never parse/build connection strings directly
- ❌ Any SQL string (`SELECT`, `CREATE`, `DROP`, `INSERT`, `UPDATE`, `DELETE`, `pg_`, `information_schema`)
- ❌ `using DbAdapter.PostgreSql;` — no engine type reaches the UI
- ❌ `using DbAdapter.Definitions;` in any UI file other than `App.xaml.cs` (composition root, needs `IAdapterCatalog` + `IModelComparer`), `Services/HtmlReportService.cs` and `ViewModels/CompareViewModel.cs` (both need `ECompareStatus`); no other Definitions role may reach the UI — talk to `IAdapterCatalog`-backed Application services for everything else
- ❌ `new PgSqlExecutor(...)`, `new PgMetadataReader(...)` — never instantiate provider types
- ❌ `PackageReference` to `Npgsql` — never add; Npgsql lives only in the plugin folder
- ❌ An engine default: no port (`5432`), username (`postgres`), TLS name (`Prefer`/`Require`/`Disable`), clipboard format id (`pg-npgsql`), system schema, dialect string, or `EDatabaseProvider` in any UI file. Defaults come from `IPlatformService.GetConnectionDefaults`; the TLS vocabulary and default format from `IConnectionTextService`; the provider identity from the import/detection result.

## Forbidden in DbClone.Application

- ❌ Provider-specific implementations (no PostgreSQL, MySQL, etc. — those go in provider projects)
- ❌ Any NuGet package beyond `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`
- ❌ `using Npgsql;` or any provider-specific import
- ❌ Interchange product types (format, transform, engine block) — the `Interchange/` folder wraps the product behind app DTOs; no product type leaks into view models or other Application files
- ✅ Shared orchestration code that all providers use (e.g. `CopyPipeline`) is allowed
- ✅ `Interchange/` folder: app-level DTOs (requests, results, progress, issues) and `IInterchangeService` / `InterchangeService` wrapper

## Interchange consumer rules (Store / Restore)

The interchange product (DatabaseInterchange) is complete on its own. DbClone consumes it through `IInterchangeService` in `DbClone.Application/Interchange/`. The rules:

1. **The product is complete; the app is a consumer.** Export, import, verification, packing and the PostgreSQL engine block all live in the interchange projects. `InterchangeService` builds a `DatabaseTarget` through `DatabaseTargetFactory` and delegates to the product.
2. **UI talks to Application, not to the product.** The UI sees `IInterchangeService` and app DTOs only. It does not reference the interchange projects, cannot parse a catalog, and cannot construct a model.
3. **`App.xaml.cs`** is the one place that calls `services.AddInterchangeServices()`. When the product arrives, it will also construct the exchange runtime over the adapter already held there.
4. **No engine mapping in DbClone.** The app contains no metadata-to-model conversion, no DDL generation, no value encoding for the catalog. If a change here would require writing those, it belongs in the product.
5. **Settings are app preferences.** Chunk size, pack-after-store, restore mode and the unsupported-object policy live in `StoreRestoreOptions` on `UserSettings`, not in the product.

## Forbidden in DbAdapter.PostgreSql (the engine block)

- ❌ References to `DbClone.Application` or `DbClone.UI` (the block depends only on `DbAdapter.Definitions` + `CanonicalModel`; the `ProjectReference` to `DbClone.Application` is removed)
- ❌ A DI container or an `AddPostgreSqlProvider()` extension — the block composes its own engine graph in a self-contained `Compose(ILoggerFactory, string pluginDirectory)` root called from its adapter constructor
- ❌ Reading platform/data files from `AppContext.BaseDirectory` — a plugin reads its own files from the `pluginDirectory` it is handed

## How to add a new database provider

A new engine is a plugin, not a product edit. No DI registration and no UI change are required.

1. Create a provider project (e.g. `DbAdapter.MySql`) that references only `DbAdapter.Definitions` and `CanonicalModel` — never `DbClone.Application` or `DbClone.UI`
2. Implement the Definitions roles (`IDatabaseAdapter`, `IDatabaseAdmin`, `IMetadataSource`, `ITableComparer`, `ICopySession`, `IConnectionStringSyntax`, `IPlatformResolver`, …) and expose a public constructor taking `(ILoggerFactory, string pluginDirectory)`; return engine-specific comparers from `CreateModelComparers()` and platform data from files inside `pluginDirectory`
3. Declare `Info.Dialect` (non-empty, unique across plugins)
4. Publish the project into `plugins/<dialect>` (e.g. add a `PublishAdapters`-style target to `DbClone.UI.csproj` running `dotnet publish` with `-o $(TargetDir)plugins\<dialect>`). The catalog discovers it at startup — the UI and Application name no engine and change nothing

The catalog binds the plugin's shared contract assemblies (`DbAdapter.Definitions`, `CanonicalModel`, `Microsoft.Extensions.*`) from the host's default context, so a loaded adapter implements the host's `IDatabaseAdapter` — a single type identity across contexts.

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

The `[Unreleased]` section must always describe the **final user-facing state relative to the last released version** — never the development history. End users read it as "what will be new in the next release", so it must not look like a log of incremental dev steps.

1. **Every user-visible change** (feature, fix, breaking change) must be covered under `## [Unreleased]` in the appropriate category (`Added`, `Fixed`, `Changed`, etc.)
2. **New feature in development** — maintain ONE bullet per feature that describes its complete current state. As the feature grows across sessions, **update/expand that same bullet**; do NOT add a new bullet per incremental change, and do NOT keep bullets for intermediate states that no longer exist
3. **Change to an already-released behavior** (fix or change of something users have today) — one bullet per issue; if the same issue is revisited, update the existing bullet instead of adding a second one
4. **Fix/change that only concerns an unreleased feature** (bug found while developing it, dialog reworked before release) — fold it into the feature's bullet; never add a separate `Fixed`/`Changed` entry. Users have never had the feature, so there is nothing to "fix" relative to the last release
5. Keep bullets concise but descriptive enough for end users (sub-points are allowed for large features)
6. Do NOT modify existing versioned sections (`## [1.0.0]`, etc.) unless explicitly asked to correct an error

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
