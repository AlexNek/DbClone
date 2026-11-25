# Changelog

All notable changes to DbClone will be documented in this file. Date format: YYYY-MM-DD

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Database engines are loaded as plugins from a `plugins` folder next to the application and are no longer compiled into it; adding a new engine means dropping its adapter into that folder — no code change in the application or UI
- **Store / Restore** — write a connected database into a portable `.rdbx` catalog folder, and restore a catalog into a connected database. **Store** sits on the source connection panel and **Restore** on the destination (Ctrl+Shift+S and Ctrl+Shift+R), with Pause and Stop live in the toolbar during a run. Progress, log, status bar and banner follow the operation's own stages and objects and end on its own reason — *Complete*, *Failed* or *Cancelled*, with the sentence the log carries — which stays on screen until Copy or Compare starts or a connection changes
  - Store captures schemas, tables with their columns, keys, indexes and the columns each covers, check constraints and triggers, views and materialized views, functions and procedures, sequences, extensions, row-level security policies, the dependency order between objects, and the rows. Rows and values stream rather than accumulate, so a database larger than memory is captured and restored inside a fixed memory budget. A binary (`bytea`) value is stored as its own file under the table's `blobs/` directory with its format detected from the bytes (JPEG, PNG, PDF) and only a reference in the row — never as hex text. Objects only PostgreSQL understands (enums, domains, composite types, publications, subscriptions) are kept as their original SQL, and a table with no usable key is stored in source order and says so. Two table names differing only in case get two distinct data folders, and a collision that cannot be resolved is a fatal export error naming both tables rather than silent data loss; extracting binaries validates the catalog folder and every output path against the root they belong to
  - Store Options sets the catalog location (with **Browse**, created when missing) and the subfolder naming pattern — `{Connection}_{timestamp}` by default, so a saved connection named **Invoice matcher** stores as `Invoice_matcher_<timestamp>` instead of the `neondb` or `postgres` a managed provider reports for every project — plus **Include table data**, **Write summary.md (statistics and relations)**, **Write SQL scripts (schema and data)** and **Largest data script file (MB), 0 = one file**. Chunk size and rows per chunk are advanced settings in `settings.json`, and table selection filters which tables are captured
  - Restore names what the target already holds before it writes and, on a populated database, offers to update it after an explicit confirmation or to restore into a new, uniquely named database; cancelling changes nothing. Objects and rows are created in dependency order, each value handed to the server in the type the source column had; the run then counts what the target holds and the rows of every table the catalog recorded, reporting a difference with both numbers. A failed step costs only the objects it names and later steps still run, except a failed schema or table step, which ends the run as failed. A catalog holding a publication or a subscription is refused before anything is written, and a subscription's statement is never stored because it would carry the publisher's credentials into a copied file. Roles and permissions are not captured, a materialized view returns as its definition without its data, and no sequence position is recorded, so restored rows keep their source ids and the first insert after a restore can collide until the sequence is advanced
  - Beside the catalog, Store writes `summary.md` — statistics per table and per schema, what is unusual about the catalog, and every foreign key as a searchable relation card register, naming no host, user or password — the relationship picture as an HTML file that travels with the folder, and the catalog's own SQL: `sql/schema.sql` with `sql/data.sql` or a numbered `sql/data/` family and a `sql/load.psql` driver, in the source dialect, every statement re-runnable, so a catalog can be applied through `psql` on a machine with no DbClone. A restore reads none of these files, so editing or deleting them cannot break a catalog, and the user manual (F1) documents both operations
- Connection text import fills what the text omits and says so: a string with no port (the shape Neon and Supabase dashboards publish) receives the engine's default port from its own platform data and reports which value was assumed, a port outside 1–65535 is kept exactly as written and flagged, an unrecognised parameter is reported as ignored, and **Import** is available for any text a recognised format understands
- Database relationship explorer — read a database's relationships as one picture, either **live** from a connection or from a stored catalog folder without connecting to it: pan and zoom the table graph, filter by schema, search a table, column, constraint or comment, and follow references in either direction
  - **View relationships** sits under both connection panels — the source panel's button reads the source, the destination panel's reads the target — and the folder button beside **Store** opens any catalog already on disk, which needs no connection at all. A live read touches metadata only and asks for no objects the picture does not draw, so a large database does not wait for them, and nothing is written back to the origin
  - Every relation is one straight arrow between the two borders it joins, drawn behind the tables, and each foreign key keeps its own point on both borders, so two constraints joining one pair stay two distinguishable lines; only a table's own loop is a curve, and a crossing reads as two relations passing rather than a fault. A bar at the child end means at most one child row may name a parent — derived from a key covering exactly the referencing columns, never guessed — and nothing there means many. Chains read top-down, a shared hub stands in the middle with its communities around it, and a catalog too tangled for any arrangement to be proven still opens as a picture, the one that came nearest to complete, with a note that its arrows are shown rather than proven
  - Each card is sized from its own text and states the figure its origin supplies for that one table — the row count the store captured, or nothing at all for a live read, whose header states once that rows are not counted. Selecting a table opens a panel describing it: columns with types and key marks, primary key, unique constraints, indexes named by the columns they cover, check constraints with their expressions, and both foreign-key lists with their actions, beside a register of every constraint in the catalog; the columns a constraint joins also appear beside its arrow on hover or selection, and clicking the empty canvas puts that selection down again — the card, its arrows and its register row are released and the panel returns to its opening words. A dashed arrow or card means the referenced table is outside the picture, a cycle is named in words in the card of every table inside it, and a large group is one box you expand by pointer or keyboard
  - The document opens in DbClone's own window, served from memory, with **Refresh** (re-reads the origin), **Open in browser** and **Save as HTML…** — which keeps a hand-moved table where the reader put it — and falls back to the system browser on a machine that cannot host it, saying why in the log. The window opens before the work is done and names each phase with an elapsed timer (*Reading the database model…*, *Arranging the picture…*) while the arrangement is computed off the UI thread, so the application stays responsive; a failure or an interrupted layout is stated there with its reason instead of leaving a stalled bar. The picture fills the window, opens on its own tables, keeps its place across a refresh, a filter change or a resize, and a table can be carried by hand with its arrows redrawn from card to card as it goes

### Changed
- An installed build writes its logs to `%LOCALAPPDATA%\DbClone\logs`, beside the app's other saved files and its `crash.log`, instead of the roaming profile — so a domain account no longer syncs them to the server — and logs at Information rather than Debug, which cuts the noise in a long run. The folder is bounded: 10 MB per file with ten files kept (about 100 MB), oldest removed first, so a long copy cannot grow it without limit

### Fixed
- An unexpected error on the UI thread no longer closes DbClone without a word: it is written to the application log and to `%LOCALAPPDATA%\DbClone\crash.log`, and one window names the error before the application ends
- Copy and Compare resolve the source and destination adapters independently, reject a cross-engine operation before any destructive work, and use adapter-owned copy connection handles for reopen and disposal
- Saved connections, groups, presets and settings are replaced atomically, and corrupt persisted data is quarantined instead of becoming a file that a later save silently overwrites
- Encrypted connection backups use authenticated AES-GCM version 2 with atomic replacement, and version 1 backups still restore
- The About window's update check reports that the check failed instead of claiming the app is up to date when the release feed cannot be reached
- The table progress panel now moves while a table is being copied, at most ten times a second, so a table of any size — not only one larger than a single batch — shows moving row progress, speed and ETA
- A copy that leaves platform schemas out no longer attempts the foreign keys that point into them: such a constraint is never sent to the server and is stated once per parent table (`SKIPPED: auth.users: 18 foreign key(s) not created — parent table is outside the copied scope`) instead of arriving as one warning per constraint in a run that still ended "Copy complete". Compare reports the same constraint as a notice — `FK not compared: <name> (parent <schema>.<table> is outside the compared scope)` — rather than a difference; constraints whose parent is compared on both sides are compared exactly as before
- A copy that lost only a function, view, materialized view or trigger ends **complete** with a count instead of claiming failure: each object the server refused is named once at warning severity (`FAIL: realtime.list_changes: permission denied for function list_changes`) and the run ends `Copy complete in 241.1s — 1 object not created (see the log)`. A table that could not be created, rows that failed to copy or validate, and a refused index, sequence or composite type still end the run as failed
- A function whose definition sets a logging parameter is now created instead of being left out: before creating functions, the copy asks for the settings privilege each function declares, so a destination role that may confer it no longer loses the function. The grant covers only the five logging parameters, is never revoked, needs PostgreSQL 15 or later on the destination, and a refusal is reported with the statement to run
- A statement that fails and is handled by its caller is written to the log once, at the severity the caller states, instead of also appearing as an error line for the executor; reading a sequence's owned column from a destination that has no such sequence is an ordinary "no value" answer, so the false `Scalar query returned null` error is gone

### Security
- Auto-update verifies the downloaded installer against the SHA-256 the release publishes for it — the asset digest GitHub records, or a `.sha256` file beside the installer — and refuses to launch a download that does not match

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

[Unreleased]: https://github.com/AlexNek/DbClone/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/AlexNek/DbClone/releases/tag/v1.1.1
[1.1.0]: https://github.com/AlexNek/DbClone/releases/tag/v1.1.0
[1.0.4]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.4
[1.0.3]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.3
[1.0.2]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.2
[1.0.1]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.1
[1.0.0]: https://github.com/AlexNek/DbClone/releases/tag/v1.0.0
