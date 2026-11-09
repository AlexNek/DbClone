# Store & Restore

Store writes a connected database into a **catalog** — a folder of files on disk. Restore reads such a folder back into a connected database. The two operations are the file-based route, next to the live database-to-database [Copy modes](copy-modes/overview.md): you can archive a database, move it between machines that cannot talk to each other directly, or inspect what a source contains before applying anything.

!!! info "Store / Restore is not a mode"
    The mode switch stays on **Copy** or **Compare**. Store and Restore are operations you start from the connection panels; while one runs, the progress area, the log, and the notification banner show its state, and they return to the active mode when it finishes.

## Where the controls are

| Control | Location |
|---------|----------|
| **Store** button | Below the **source** connection panel — the database that is captured |
| **Restore** button | Below the **destination** connection panel — the database a catalog is applied to |
| **Store Options** | Toolbar popup: catalog location, subfolder pattern, data inclusion, packing |
| **Pause / Resume**, **Stop** | Toolbar, enabled while a Store or Restore operation runs |
| ++ctrl+shift+s++ / ++ctrl+shift+r++ | Start Store / Start Restore — see [Keyboard Shortcuts](keyboard-shortcuts.md) |

Both operations need a connection that names a **specific database**. A connection that points at a server only is refused with a banner saying so; open the [Connection Manager](connections/managing-connections.md) and pick the database first.

## Storing a catalog

1. Select the source connection.
2. Press **Store** (++ctrl+shift+s++).
3. In the folder browser, choose where catalogs live. The folder is created if it does not exist and becomes the starting point next time.
4. DbClone writes the catalog into a subfolder named by the [subfolder pattern](#store-restore-options) and logs the full path.

The store runs in the order the format defines: schemas, tables and columns, views, routines, sequences, extensions, the dependency edges between objects, row-level security content, the PostgreSQL-only objects, then the table rows. Rows stream from the server with a cursor and are written one at a time to disk, so a large table does not have to fit in memory. The `ChunkBytes` and `RowsPerChunk` settings decide where a table's rows are split into chunk files — they are not a limit on how much is held while the table is written.

Binary values (`bytea` columns) are always stored in individual files under the table's `blobs/` directory, regardless of size. The CSV row contains only a reference (`@blob:<path>:0:<length>`), never a hex payload. Each non-NULL value gets one file named from the CSV chunk, encoded column name, row index and detected format; NULL values create no file. The detected format (JPEG, PNG, PDF, etc.) is derived from the bytes themselves and recorded in the file name, never from a `content_type` column. This makes every value directly addressable and avoids a shared container or binary data in CSV. When the files are written, the catalog is re-read and checked against its own index before the operation reports success.

| Result you see | Meaning |
|----------------|---------|
| **Store Complete** (green) | The catalog is usable; the banner gives table count, row count, elapsed time, and the folder |
| **Store Complete** with warnings | Files were written, but the capture recorded something it could not carry — each note is in the log with its code |
| **Store Incomplete** | You stopped it mid-run. The folder is **not** usable; run Store again and let it finish |
| **Store FAILED** | The run stopped with an error and the reason is in the log. A folder from a failed run is not a catalog until its index was written — run Store again |

## What the catalog folder holds

```
<MyCatalogFolder>/
├── manifest.json          the file index: format version, source dialect, object and row
│                          counts, and a checksum per file
├── catalog.json           format version, the sections present, the table count
├── export.json            when the catalog was written and by which producer
├── schema/                one JSON file per section:
│   ├── schemas.json       schemas
│   ├── tables.json        tables, columns, keys, constraints, indexes, triggers, partitions
│   ├── columns.json       the column inventory
│   ├── views.json         views and materialized views
│   ├── routines.json      functions and procedures
│   ├── sequences.json     sequences
│   ├── extensions.json    extensions
│   ├── relationships.json which object depends on which
│   └── security.json      row-level security policies (only when the source had any)
├── native/                objects only PostgreSQL understands, as their original SQL
│   └── postgresql.json
└── data/<schema>/<table>/  table.json (column list, row count, bounded maps) + checksums.sha256
    ├── data.00001.csv       UTF-8 CSV with a complete header and streaming rows
    ├── data.00002.csv       additional chunks when the table exceeds limits
    └── blobs/                one file per non-NULL binary value
        ├── data.00001.image_data.000000.png   row 0's detected PNG
        ├── data.00001.image_data.000001.jpg   row 1's detected JPEG
        └── data.00001.image_data.000002.bin   an unrecognized binary value
```

Restore checks every file against the checksums in `manifest.json` before it touches the database, so a partially copied or edited folder is caught at that point rather than applied broken. Treat the folder the way you would treat a database dump: it holds your data and your schema as plain text.

## Restoring a catalog

1. Select the destination connection — an **empty** database.
2. Press **Restore** (++ctrl+shift+r++).
3. In the folder browser, select the catalog folder **itself**, not the folder that contains it.
4. DbClone verifies the files, checks that the target is empty, then applies the catalog.

| Result you see | Meaning |
|----------------|---------|
| **Restore Complete** (green) | The catalog was applied and the check after it found nothing to report; the banner gives the object count, the rows written and the elapsed time |
| **Restore Complete** with warnings | Objects were applied, and the restore recorded notes about what it did not carry — read them in the log |
| **Restore Complete** with warnings naming counts | The objects were applied, but the check after the data found the target holding a different number than the catalog recorded. Each difference is an error line in the log naming both numbers |
| **Restore FAILED** — target is not empty | The log names what the database already holds. A restore never cleans a target; empty the database first, or restore into a new one |
| **Restore FAILED** — "…object(s) this restore cannot create…" | The catalog holds a publication or a subscription. **Nothing was written** — the target is untouched, and the log names every such object |

!!! note "Reading a failed restore"
    A restore that ends in failure reports `Restore FAILED` in the status bar and writes the exact reason — the objects it refused, or what the target already contains — as an error line in the log pane.

## Current state of the restore

The restore is being finished in stages, and it refuses rather than reporting a success it did not achieve. Today:

| Catalog content | On restore |
|-----------------|-----------|
| Schemas, custom types (enum, composite, domain), sequences, tables with their columns, defaults, generated and identity clauses, keys, constraints, partitions, functions, procedures, views, foreign keys, hand-made indexes, triggers | Applied, in dependency order; the trigger's own condition, column list and arguments come back from the statement the server reported, and a trigger the source had switched off is switched off again |
| Materialized views | The definition is created; the cached rows are not filled |
| Index that a primary key or unique constraint already made | Left alone — the constraint created it |
| Table rows | Written back in batches of at most 1 000 rows or about 2 MB of stored text, each value handed to the server in the type its source column had — so a column of a custom enum, domain or composite type comes back as that type instead of failing. A generated column is left out of the insert and the server computes it. Timestamps come back as the instants that were captured, whatever time zone the target server runs in |
| What the restore checks after the data | It counts what the target now holds — schemas, tables, views, materialized views, sequences, functions, procedures, enums, domains and composite types — and reads the row count of every table whose data the catalog recorded. A difference is reported as an error naming both numbers, and a kind this check cannot count is named as unchecked rather than passed, so a green summary always stands for a check that ran |
| Indexes, triggers, constraints and column shapes | Not compared after the restore — the check counts objects and rows, not definitions. A stage that fails to create something stops the whole restore with an error instead, so nothing that failed to be created is quietly accepted |
| Publications and subscriptions | Refused before anything is written, by name. A subscription's statement is not even stored, because `CREATE SUBSCRIPTION` embeds the publisher's connection string and a catalog is a folder that gets copied — its name, the publication it reads, and a note about what was left out are recorded instead |
| Roles and grants | Not captured from the source yet, so not restored |
| Triggers while the rows are written | The structure is created before the data is applied, so a trigger the source had fires for the restored rows just as it would for any other insert — an audit trigger on the source writes its audit rows on the target as well |
| Sequence current values, sequence `OWNED BY` links, the `GENERATED ALWAYS` flavor of an identity column, table comments, tablespaces, fill factors, per-column statistics targets | Not captured — the catalog records the loss where it can, and the restore cannot recreate what it never held. The missing sequence position is the one you can hit: the rows come back with the ids the source gave them while the sequence starts at its beginning, so the first row inserted after a restore can collide with a restored one until the sequence is moved forward. The identity flavor is why those ids can be inserted at all — the restore writes `GENERATED BY DEFAULT`, which accepts an explicit value that a `GENERATED ALWAYS` column would have refused |

The preserved PostgreSQL-only statements are applied only when the target reports the same dialect the catalog was written from; a folder from another engine is refused instead of executed.

## Store & Restore options

**Store Options** on the toolbar holds:

| Setting | Effect |
|---------|--------|
| **Catalog Location** | Root folder for catalogs, with **Browse…**. The last folder you picked is used as the starting point |
| **Subfolder Pattern** | Folder name created under the root. `{Database}` is the source database name, `{timestamp}` is `yyyy-MM-dd_HH-mm`. Leave it empty to write straight into the root folder |
| **Include table data** | Off stores the schema only, and restoring such a catalog writes the structure and no rows. On stores the rows and the restore writes them back |

These values live in `settings.json` under `StoreRestore`. Catalogs are currently stored as directories. ZIP packaging is a separate planned feature and is not currently exposed as a Store option.

| Setting | Default | Effect |
|---------|---------|--------|
| `ChunkBytes` | 8388608 (8 MiB) | Maximum byte size per chunk file; the catalog writer honours this limit when splitting table rows across chunk files |
| `RowsPerChunk` | 50000 | Maximum row count per chunk file; the 1 000 000-row Excel ceiling is enforced on top of this setting |
| `SnapshotConsistency` | `consistent` | The row read uses a consistent snapshot across all tables when set to `consistent`, or read-committed isolation when set to `readCommitted` |
| `UnsupportedFeaturePolicy` | `fail` | When `fail`, an unsupported object stops the restore before any write; when `warn`, the restore skips it and records an issue |

!!! note "Table selection narrows a catalog"
    A [table selection](connections/table-selection.md) is applied end-to-end: the catalog captures only the tables the selection includes, and the log names the excluded tables when Store starts.

## See also

- [Copy Modes](copy-modes/overview.md) — the live database-to-database route
- [Options Reference](options.md) — copy, verify, and platform settings
- [The Main Window](getting-started/main-window.md) — where the panels and the toolbar are
- [Troubleshooting](troubleshooting.md) — connection and permission problems
