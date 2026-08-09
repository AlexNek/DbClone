# Changelog

All notable changes to DbClone will be documented in this file. Date format: YYYY-MM-DD

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
[1.0.1]: https://github.com/AlexNek/DbClone/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.0
