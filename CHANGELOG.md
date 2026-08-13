# Changelog

All notable changes to DbClone will be documented in this file. Date format: YYYY-MM-DD

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
