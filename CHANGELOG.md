# Changelog

All notable changes to DbClone will be documented in this file. Date format: YYYY-MM-DD

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Database engines are now loaded as plugins from a `plugins` folder next to the application and are no longer compiled into it; adding a new engine means dropping its adapter into that folder — no code change in the application or UI
- Store / Restore operation — writes a connected database into a `.rdbx` catalog and restores one into a connected database, built on the DatabaseInterchange product contract
  - Store button on the Source connection panel and Restore button on the Destination connection panel — always visible, clicking either button sets the direction and starts the operation; keyboard shortcuts Ctrl+Shift+S and Ctrl+Shift+R
  - Pause/Stop controls always visible in the toolbar, enabled when a Store/Restore operation is running
  - Progress, log, and notification banner automatically show Store/Restore state when an operation is running, otherwise show the active mode (Copy / Compare) state
  - Store Options panel with catalog location, subfolder naming pattern, data inclusion and the catalog summary file; the naming pattern builds the catalog folder from `{Connection}` (the selected saved connection's name, so a connection called **Invoice matcher** stores as `Invoice_matcher_<timestamp>` instead of the `neondb` or `postgres` name a managed provider gives every project), `{Database}` and `{timestamp}`, and defaults to `{Connection}_{timestamp}` — `{Connection}` falls back to the source database name when no saved connection is selected, spaces and characters a folder name cannot hold become `_`, a name Windows reserves as a device is prefixed with `_`, and an over-long name is cut so the timestamp tail survives; chunk size, rows per chunk, snapshot consistency and unsupported-feature policy are read from `settings.json` and have no control in the panel yet
  - User manual section "Store & Restore" (F1) documenting both operations, what the catalog folder holds, and what a restore does and does not apply today
  - Catalog folder is picked in a folder browser when Store or Restore starts, the default folder can be changed in settings with a Browse button, and it is created automatically when it does not exist
  - Store captures the connected database into the catalog: schemas, tables with their columns, primary keys, indexes, check constraints and triggers, views and materialized views, functions and procedures, sequences, extensions, the dependency order between objects, row-level security policies, and the table rows — streamed with a server-side cursor so large tables do not have to fit in memory. Binary values (`bytea` columns) are always stored in individual files under the table's `blobs/` directory, never inline as hex; the row file keeps a reference (`@blob:<path>:0:<length>`) to the exact value file. The detected format (JPEG, PNG, PDF, etc.) is derived from the bytes themselves and recorded in the file name, never from a `content_type` column. Each file is directly usable and restore reads the referenced bytes without rebuilding hex text. Objects that only PostgreSQL understands (enums, domains, composite types, publications, subscriptions) are preserved as their original SQL and are re-applied only to a PostgreSQL catalog; tables without a usable key are stored in source order and say so. A partitioned table is stored with its parent, its strategy and its partition bound, and a sequence that belongs to a column says that the link cannot be carried yet. Restore into a connected database binds the bytes of a container value straight to the target as a `bytea` parameter read from a stream over the referenced range — they are never converted back to hex and never held as one text value on the way in — while a binary value small enough to have stayed inline keeps its existing inline token. Restore is still being implemented, and it stops rather than pretending: it checks that the target is empty and reports what is already in it, then creates the catalog's schemas, custom types, sequences and tables — each table with its columns, defaults, generated and identity clauses, primary key, unique and check constraints, and each partition attached to the table it partitions — followed by functions and procedures, views (a materialized view comes back as its definition only, without its data), the foreign keys between tables, the indexes the source created by hand — an index that a primary key or unique constraint already made is left alone — and the triggers, each restored from the statement the server reported for it so its condition, its column list and the arguments it passes to its function come back intact; a trigger the source had switched off is switched off again. All of it happens in dependency order. The PostgreSQL-only objects are restored as well: the enums, composite types and domains the source declared are created before the tables that name them, from the captured statement, in the order one kind of type can depend on another — so a table with a column of a custom type now builds instead of failing with "type does not exist", a type name the source kept in mixed case comes back as the same name instead of a lower-cased one, and a domain keeps the check the server reported for it. A catalog that holds a publication or a subscription is not restored at all: the operation stops before it writes anything and names every object that nothing on the target can create, rather than filling the target first and then reporting the one object it left out. A catalog that holds any table also writes its rows back: the data files are read in batches and every stored value is handed to the server in the type the source column had, so a table whose column is a custom enum, domain or composite type comes back with its rows instead of failing on the way in, a column the server computes for itself is left to compute, and a timestamp keeps the instant it held even when the target server runs in another time zone. The restore then checks its own work: it counts what the target now holds — schemas, tables, views, materialized views, sequences, functions, procedures, enums, domains and composite types — and reads the row count of every table whose data the catalog recorded, reporting a difference as an error that names both numbers and a kind it cannot count as unchecked rather than passed, so a clean summary always stands for a check that actually ran. Because a sequence's position is not in the catalog, restored rows keep the ids the source gave them while the sequence starts at its beginning, so the first row inserted after a restore can collide with a restored one until the sequence is moved forward. A subscription's statement is no longer written into the catalog at all, because `CREATE SUBSCRIPTION` embeds the publisher's connection string and a catalog is a file that gets copied; its name, the publication it reads and a note saying what was left out are recorded instead. Database roles and permissions are still not read from the source at all (tracked in `refactors/04/implementationChecklist.md`)
  - `summary.md` written into the catalog folder by Store and replaced on every store: statistics per table and per schema, a short list of what is unusual about the catalog (tables with no primary key, the tables the rest leans on, cyclic groups, foreign keys that leave the catalog, tables that take part in no relationship, pairs joined more than once), and every foreign key the catalog captured written out as a searchable register with a relation card for each table — nothing is dropped because the schema is large. Deliberately no diagram: hundreds of lines crossing a picture cannot be read, while the same facts in a list can be searched. The file names objects only — no host, user, password or connection string — so it is as safe to commit beside a catalog as the catalog's own text, and Restore never reads it: deleting, editing or replacing it cannot break a catalog. Switched off with the **Write summary.md** option in Store Options
  - The Store result now reports the rows, bytes and chunks the catalog really holds instead of zeros, and the log names the path of the summary file it wrote; stopping Store while the summary is being written stops only that file and still reports the finished catalog as complete
- Catalog verification now includes a tenth integrity check: every table declared in the catalog's tables section has a corresponding data folder, and every data folder is claimed by a table in the section — this catches the silent data loss that case-colliding table names could cause on case-insensitive filesystems
- Format version bumped to 3: row chunks are UTF-8 CSV files named `data.NNNNN.csv`, each with a BOM and complete column header; `manifest.json` records `data.encoding: "csv"` and requires a version 3 reader, while legacy NDJSON and older catalogs are refused. Every non-NULL binary value is stored in its own `blobs/<chunk>.<column>.<row>.<suffix>` file and the CSV carries only a reference. `table.json` contains bounded row and format counts, while `checksums.sha256` lists every row and blob file.

### Changed
- Internal architecture refactored onto a single-adapter boundary (see `refactors/08/adapterConceptDesign.md`): the application now meets the database engine through one contract (`DbAdapter.Definitions`) with seven role interfaces (`IMetadataSource`, `IDatabaseAdmin`, `IDatabaseCleaner`, `ITableComparer`, `ICopySession`, `IConnectionStringSyntax`, `IPlatformResolver`), filled by `DbAdapter.PostgreSql`. Orchestration (connection verification, metadata reading, table filtering, permissions, backup, target cleaning, copy execution, result mapping) moved from the UI into `DbClone.Application` behind `ICopyWorkflow`, `ITableCatalogService`, and `IComparisonService`. The engine is reached only through an adapter catalog discovered from the `plugins` folder — no UI or Application file names an engine type, and `App.xaml.cs` registers the catalog rather than constructing an adapter. `CanonicalModel` holds the shared language (model records, copy options/results, platform data, enums); `DbAdapter.Definitions` holds the boundary DTOs (`DatabaseTarget`, `CopyRequest`, `AdapterInfo`) and the role interfaces. No user-visible behavior changes — Copy, Compare, and Backup work exactly as before
- Store/Restore streaming refactor (see `refactors/12/streamingDataConceptDesign.md`): catalog writing and reading now stream rows one at a time instead of accumulating chunks in memory — a database of any size can be captured and restored inside a fixed memory budget. Chunk byte limit (`ChunkBytes`) and row limit (`MaxRowsPerChunk`) from settings are now honoured end-to-end; table selection filters which tables participate in Store; table folder names are resolved across case-colliding siblings before any folder is created, so `MixedCase` and `mixedcase` in the same schema produce two distinct folders instead of one overwriting the other; a collision is a fatal export error naming both tables, never a silent loss. Catalog inspection reads row counts from the manifest instead of re-parsing every row file

### Fixed
- Importing a connection string is no longer a dead end when the text omits a value, and nothing the import assumes stays silent: a string with no port (the shape Neon and Supabase dashboards publish) gets the engine's default port — taken from the adapter's own platform data — and states that the port was not specified and which value will be used; a string that names no server is taken as the local one and says the same; a port outside the range any server can listen on (1–65535) is kept exactly as written but flagged. **Import** is now enabled for any text a format understands, so a warning can never leave the button disabled with nothing to explain it. The same string imported from the clipboard or from a file now reports what it had to assume, or why nothing was imported, instead of storing a half-filled connection or doing nothing at all
- Table selection in Store/Restore is no longer decorative: the `Tables` and `ExcludedTables` filters from the UI reach the catalog writer through `ExportOptions`, so selecting specific tables now captures only those tables
- Case-colliding table names (e.g. `MixedCase` and `mixedcase` in the same schema) no longer silently overwrite each other on case-insensitive filesystems: the writer resolves collisions before creating folders and refuses to continue if a collision is detected, and the folder each table was written into is recorded in the catalog so a restore reads every one of those tables' own rows instead of guessing the name again — previously the second table's data was lost on export, and its rows were read back from its sibling on restore

## [1.1.1] - 2026-08-12

### Fixed
- Update install errors caused by file-access denial (e.g. antivirus blocking the downloaded installer) now show the same recovery guidance as other access errors instead of a generic failure message

### Changed
- Update banner now shows download progress (spinning indicator), disables the Update button while downloading, and displays a clear error message with Retry/Dismiss options if the installer download or launch fails (e.g. antivirus blocking)
- The update banner's close button is now disabled while the installer is downloading, so the progress/error state cannot be dismissed mid-download

## [1.1.0] - 2026-08-12

### Added
- Manual table selection: choose which tables Copy, Compare, and Backup process for a source database
  - Selection dialog with schema tree, search, sorting, table size column, and a foreign-key relationship explorer
  - Named presets stored per source connection + database with automatic restore of the last-used preset; unsaved (dirty) modifications survive application restart
  - Validation summary before applying a selection (dangling foreign keys, dependent views, orphaned partitions)
  - FK-aware behavior: unchecking a parent table automatically deselects its dependent child tables (recursively); re-selecting a child whose parent is excluded highlights the row with a warning
  - Compare ignores target tables outside the selection; views depending on excluded tables are reported as skipped
  - Copy and Backup clean only the selected tables on a populated destination, with an explicit choice: replace only the selected tables, or clear the entire destination
  - Resume/Update modes are blocked with an explanation while a non-default selection is active (they require "All Tables")
  - Destination table overview: read-only dialog listing all tables on the output database (schema tree, search, sorting, size column)
  - Destination database picker: when the destination has no database name, browse the server's databases and pick one directly from the main panel

### Fixed
- Sequence sync no longer fails for mixed-case names (e.g. serial sequences of `"MixedCase"` tables): sequence and owner-table identifiers are now quoted in `setval`/`pg_get_serial_sequence` calls, while the owner-column is passed unquoted so it matches the catalog literally
- Serial sequence sync no longer produces false "has no sequence on destination" warnings — the stage now resolves serial sequences by name and establishes the missing `OWNED BY` link on the destination
- Copy summary now lists each warning inline with its stage context (e.g. `SKIP [Extensions]: vector`) instead of "review warning entries above"
- Tables that fail to create (e.g. due to unsupported types or unavailable extensions) are now individually reported in the final warning summary with their specific error reason
- CopyData stage now explicitly logs which tables it skips because their creation failed earlier, instead of silently excluding them
- Copy summary "Tables:" count now includes all successfully processed tables (including empty ones), not just tables that had rows — previously "Tables: 3" when 16 tables were copied but only 3 had data
- Copy is now blocked with a clear error when a connection is missing a database name — Full/Resume/Update require a destination database (backup-only connections can only use Backup mode), and all modes require a source database; previously such copies reported success without copying anything
- Error banner and status display are now cleared when the connection or connection group is changed — previously an error from an earlier run remained visible after switching connections
- Connections, connection groups, table selection, and copy/compare options can no longer be changed while an operation is running — the controls are disabled until the operation finishes
- Connection failure error now shows which side (source/destination) failed and the configured host:port/database, instead of only the raw TCP error with a resolved IP
- Table selection now correctly excludes objects (sequences, triggers, policies, foreign keys) owned by partitions of excluded parent tables — previously only the explicitly excluded table's objects were filtered, leaving orphaned-partition dependents behind
- Table selection now skips materialized views that depend on an excluded table (same as ordinary views) — previously they were retained and could fail during creation
- Skipped-table tracking in the copy pipeline now uses the typed `TableId` key instead of fragile formatted strings — previously mixed-case table names could slip through and attempt data copy after their creation had already failed

## [1.0.4] - 2026-08-09

### Fixed
- Connection import now handles unencoded special characters (`@`, `#`, `&`, `?`, `:`, `/`, spaces) in passwords when pasting a URI — no manual percent-encoding required
- Connection import now resolves the correct host when a URI contains `@` in both the password and a query-parameter value (scored candidate parsing instead of rightmost-`@` only)
- Connection import now rejects URIs with invalid ports (0, above 65535, oversized numbers) as unparseable instead of failing with an exception
- Connection string parsing now honors the `sslmode` query parameter in URIs (e.g. `?sslmode=require`) instead of always defaulting to Prefer

## [1.0.3] - 2026-08-08

### Added
- "What's new" button in the update info bar — opens release notes so users can review changes before deciding to update

### Fixed
- Indexes skipped due to failed parent tables are now explicitly reported as "SKIPPED" in the stage output instead of being silently omitted or mislabeled as "FAIL"
- SyncSequencesStage now documents skipped owned sequences within its own stage output instead of only showing them as orphaned FAIL lines at the end of the log
- Index stage ObjectsProcessed count now includes skipped indexes so the log line matches the header bar total
- Documentation corrections to match the shipped app: installer switches and install location, log file paths, options panel contents, backup naming pattern and Backup Name field, connection groups, import/export formats, report export buttons
- Update button remains visible after closing the update info bar — extracted into a self-contained `UpdateInfoBarView` component with its own ViewModel


## [1.0.2] - 2026-08-08

### Fixed
- Tables status indicator in the top info bar now correctly shows a red error when table creation fails (previously overwritten to green by the subsequent CopyData stage)

## [1.0.1] - 2026-08-07

### Added
- Help button in toolbar (and F1 shortcut) to open online documentation

## [1.0.0] - 2026-08-07

### Added
- Initial release
- Full schema cloning — tables, views, functions, triggers, sequences, enums, domains, composite types, constraints, RLS policies
- Four copy modes: Full, Resume, Update, Backup
- Platform-aware `.platform` definition files — auto-detect Supabase, Aiven, Neon, vanilla PostgreSQL
- Extension-aware DDL — detects extension-owned objects via `pg_depend` and skips re-creating them
- Dependency-ordered DDL — topological sort with cycle detection
- Data copy with real-time per-table progress, ETA, row counts
- Resume mode — compares row counts, copies only missing/mismatched tables
- Enhanced validation — data verification (row count, checksum, full) plus object count validation
- Database comparison — bidirectional structural comparison with detailed DDL diffing
- Connection manager — save, organize, color-code, import/export with optional AES-256 encryption
- Platform type dropdown — auto-detects host and fills connection defaults
- Dark/light theme support
- Auto-update — checks GitHub Releases on startup; non-blocking banner notification
- Fluent Design UI powered by WPF-UI

[Unreleased]: https://github.com/AlexNek/DbClone/compare/v1.0.4...HEAD
[1.0.4]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.4
[1.0.3]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.3
[1.0.2]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.2
[1.0.1]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.1
[1.0.0]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.0
