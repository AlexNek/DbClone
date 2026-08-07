# Changelog

All notable changes to DbClone will be documented in this file. Date format: YYYY-MM-DD

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/AlexNek/DbClone/compare/v1.0.0...HEAD
[1.0.1]: https://github.com/AlexNek/DbClone/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.0
