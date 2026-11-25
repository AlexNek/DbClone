# Store & Restore

Store writes a connected database into a **catalog** — a folder of files on disk. Restore reads such a folder back into a connected database. The two operations are the file-based route, next to the live database-to-database [Copy modes](copy-modes/overview.md): you can archive a database, move it between machines that cannot talk to each other directly, or inspect what a source contains before applying anything.

!!! info "Store / Restore is not a mode"
    The mode switch stays on **Copy** or **Compare**. Store and Restore are operations you start from the connection panels; while one runs, the log and notification banner show its state, and the progress area remains indeterminate because row, byte, and resume checkpoints are not yet exposed by the interchange product.

## Where the controls are

| Control | Location |
|---------|----------|
| **Store** button | Below the **source** connection panel — the database that is captured |
| **Restore** button | Below the **destination** connection panel — the database a catalog is applied to |
| **View relationships** | Below **both** connection panels — the source panel's button opens the source database's relationships, the destination panel's the target's. Either one reads that database live, so nothing has to be stored first. The folder button next to the source panel's button opens any catalog already on disk — see [Relationship View](relationship-view.md) |
| **Store Options** | Toolbar popup: catalog location, subfolder pattern, data inclusion, packing |
| **Stop** | Toolbar, enabled while a Store or Restore operation runs; Store and Restore do not yet support pausing or resuming a partial catalog |
| ++ctrl+shift+s++ / ++ctrl+shift+r++ | Start Store / Start Restore — see [Keyboard Shortcuts](keyboard-shortcuts.md) |

Both operations need a connection to a server. Store needs a **specific source database**. Restore can use a specific destination database, or a server-only destination when you choose to create a new restore database.

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
├── summary.md             the plain-text account of this catalog — see [Reading the catalog summary](#reading-the-catalog-summary)
├── summary.md.tmp         left only by a write that was interrupted; harmless, and you may delete it
├── <folder>.html          the relationship picture of this catalog, named after the folder — see [Relationship View](relationship-view.md)
├── sql/                   the catalog as the source engine's own SQL — see [The catalog's own SQL](#the-catalogs-own-sql)
│   ├── schema.sql         every object, in the order a restore creates them, re-runnable
│   ├── data.sql           one block per table in dependency order, re-runnable, ending with the key validation
│   ├── data/              the same rows when they do not fit one file — either this or data.sql, never both
│   │   ├── 0001.sql       the rows in load order; the file boundary falls where the size you set is reached
│   │   └── 0002.sql       a table that outgrew the previous file continues here, in its own transaction
│   └── load.psql          written only when there is a data/ family: includes its files in the order they run
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

## Reading the catalog summary

`summary.md` is a plain-text account of what the store captured. It is written into the catalog folder and replaced on every store, so it always describes the folder it sits in. The file is Markdown: it opens in any editor, it reads on GitHub, and a change between two stores shows up as a line in a diff.

| Section | What it gives you |
|---------|-------------------|
| **Catalog observations** | The few things worth knowing before the numbers: tables with no primary key, the tables the rest of the catalog leans on, the cyclic groups, foreign keys that point outside the catalog, tables that take part in no foreign-key relationship, and pairs joined by more than one constraint. Each line is a count plus a pointer to the section that lists the items in full. A catalog with nothing unusual says so in one line |
| **Global statistics** | Three figures in bytes that mean different things: *data bytes* (the CSV rows and the binary files), *metadata bytes* (the JSON that describes the objects, `manifest.json` and `export.json` included), and **catalog bytes** — the two together. **Catalog bytes** is the number to compare against the folder. A file manager will report slightly more, because it also counts `summary.md`, the `sql/` scripts and rounds every file up to the disk's cluster size |
| **Statistics per table** | One row for every table the catalog holds — its rows, its bytes, its chunk files, and the order its rows were stored in |
| **SQL scripts** | One row for every file the store wrote beside the catalog — the schema, then each data file — with its size, statement count and rows, which of them the keys are validated at the end of, the layout the store chose and why, the command that loads them, the guarantee that they may be run twice, and what they deliberately do not do — see [The catalog's own SQL](#the-catalogs-own-sql). Absent when the option is off, and one sentence naming the reason when nothing was written |
| **Relationships** | Every foreign key the catalog captured, once per constraint in a register you can search, and again in the card of each table it joins. Cyclic groups, heavily referenced tables and duplicated pairs are named |

A table listed under `row order: sourceOrder` did have its rows captured — all of them. What is not promised is the order they come back out in next time, because the table has no key to read by. The rows are there; only their sequence may differ between two stores.

Being listed under *tables that take part in no foreign-key relationship* is not the same as being called unrelated. Views, routines and your own application code can read such a table; the catalog simply records no constraint between it and another table.

!!! info "What the summary describes"
    It accounts for **the objects the catalog captured**, not for the source database: tables a [table selection](connections/table-selection.md) excluded appear nowhere in it, and a foreign key whose target was excluded is listed as pointing outside the catalog rather than as a missing relation.

The file names objects only — schemas, tables, columns, constraints — plus the numbers measured from the folder. It contains no host name, no user name, no password and no connection string, so it is as safe to commit beside a catalog as the catalog's own text.

Nothing in it is cut because a schema is large. Every captured constraint gets its register row and its two card lines: a table referenced by 400 others gets 400 lines you can scroll through and search, which is more usable than 400 lines crossing a picture. The cost is honesty about length — the file is exactly as long as the catalog's facts, so a very large schema produces a very large text file that searches well and opens slowly in an editor.

### Relationships: why the summary carries no diagram

A diagram of a schema with heavy relationships cannot be read as one sheet: hundreds of boxes, lines crossing, nothing to search. So the summary lists every foreign key once in a table you can search and again in both tables' own cards, and drops nothing because the schema is large. Cyclic groups, overloaded lookup tables and duplicate pairs are called out by name. A short outline walk is printed only where it can be complete; where one would become a hairball, the report says that instead of printing half of it.

A picture of the same facts does exist, as a separate document you ask for — see [Relationship View](relationship-view.md). It draws every table and every relation the catalog holds, however large the schema, so nothing is cut because a catalog is big.

!!! warning "Restore never reads this file"
    `summary.md` is not part of the catalog's content: it is not listed in `manifest.json`, never hashed, never verified and never applied. Deleting it, editing it or replacing its text cannot break a catalog — the same folder restores identically with the file present, absent or full of nonsense.

Stopping a store while the summary is being written stops only the report. The catalog itself was already finished at that point, so the run is reported as complete with its real counts and no summary path; press Stop at any earlier stage and the folder is genuinely incomplete, as the table above says.

## The catalog's own SQL

With **Write SQL scripts** on — the default — a store also writes the catalog as SQL beside it, in `sql/schema.sql` and `sql/data.sql`; when the rows do not fit the size you picked, `data.sql` becomes a numbered family under `sql/data/`. Both shapes are written in the dialect the source database speaks: its own type names, the index, view, trigger and function text the store captured from the server, and the same `'text'::type` literals a restore already sends. A catalog can therefore be handed to `psql` or any SQL client on a machine that has no DbClone.

Run `schema.sql` first and `data.sql` second. A catalog that holds no rows gets no data file; the closing statements then sit at the end of `schema.sql`, so running the files in name order always finishes the job.

### One file, or a numbered family

`sql/data.sql` is written when the rows fit the **Largest data script file** size — 32 MB unless you change it. When they do not fit, the same rows are written as a numbered family instead, beside a `load.psql` that runs them in order:

```
sql/
├── schema.sql
├── load.psql
└── data/
    ├── 0001.sql
    ├── 0002.sql
    └── 0003.sql
```

The two shapes never appear together: a store that writes the family removes the `data.sql` an earlier store left there, and a store that writes one file removes the `data/` folder the same way. Both hold the same statements in the same order — only where the file boundary falls changes.

What decides it: the store estimates the `INSERT` bytes each table's rows will take, packs whole tables into a file in load order — a table the rest of the rows depend on comes first — and opens the next file as soon as the next statement would take the current one past the size. A table whose own estimate exceeds the size is written in parts: its block is closed, the next file started, and the block reopened with `-- part N` in its banner. The size bounds the file itself, not just its rows: the preamble and header at the top of the first file and the key validation at the end of the last one count against it too, so no file passes the size — with one exception, a single `INSERT` bigger than the size is written whole into a file of its own rather than cut in half. And because the shape follows that estimate, a store whose rows fit one file keeps `data.sql` even when the finished file turns out larger than the size.

**`0` means one file.** Set the size to `0` and the store writes `sql/data.sql` exactly as it did before the setting existed, whatever the catalog weighs.

**Load it** with one command — `psql -f sql/load.psql`. The driver includes `schema.sql` and then each numbered file in the order the store wrote them, resolving every include against its own folder. It deliberately sets no `ON_ERROR_STOP`: add `-v ON_ERROR_STOP=1` yourself when you want a run to stop at the first error. With a single `data.sql` there is no driver and no include to get wrong — `psql -f sql/schema.sql` then `psql -f sql/data.sql`.

**Each part is its own transaction.** A table written in several files commits part by part, so a failure part-way through leaves every earlier part — and every earlier file — applied and re-runnable, and the same `psql -f` command picks up where it stopped without duplicating a row. It is never all-or-nothing across the whole catalog; a catalog loaded this way is complete only when the run finishes.

**What the size is not for.** It is the size a client, an editor and a chat window can open; it is not a substitute for a real dump of a multi-gigabyte database, which belongs in `pg_dump` and its custom format. The bound this setting respects is the editor that opens the file, not the server that loads it: PostgreSQL accepts a statement of up to 1 GB, and DbClone closes its own batch at 1 000 rows or about 2 MB of text, so the size you pick is nowhere near a limit the server has.

Every statement may be run again: a second run creates no object and adds no row.

| Object | How it is written |
|--------|-------------------|
| Schema, extension, sequence, table, partition, materialized view, index | `CREATE … IF NOT EXISTS` — an object that already exists is left exactly as it is. An extension is installed by the name and the schema the catalog recorded, with the version of the source deliberately not pinned, so a target that holds another version of it still loads |
| View | `CREATE OR REPLACE VIEW`; a view whose column list changed still fails, with the server's own message naming it |
| Enum, composite type, domain | The captured statement inside a block that ignores a duplicate object |
| Function, procedure | The captured text, which is already `CREATE OR REPLACE` |
| Foreign key | `DROP CONSTRAINT IF EXISTS`, then `ADD CONSTRAINT … NOT VALID`; every key is validated at the end of the last file |
| Trigger | `DROP TRIGGER IF EXISTS`, then the captured statement |
| Table rows | `INSERT … ON CONFLICT DO NOTHING` when the table has a primary key or a unique key that every row must fill — every column of it declared `NOT NULL`, and no `WHERE` narrowing it; `INSERT … SELECT … WHERE NOT EXISTS` otherwise, so the whole row is the identity |

Each table's rows form a block with its own transaction — a table written in parts is one transaction per part — and the blocks are written in dependency order: a table the rows of a later block point at comes first, whatever the names sort like. A failure therefore leaves every earlier block complete and the file re-runnable from its start. The two settings the literals depend on — `TIME ZONE 'UTC'` and `standard_conforming_strings = 'on'` — are set at the top of each file and again inside every table block. The rows are read from the catalog and handed to the file one batch at a time, so a table larger than memory still writes one statement after another instead of being held to the end.

What the files deliberately do **not** do:

- **Reconcile an existing table.** A table that is already there keeps its columns, keys and checks; nothing is altered or dropped to make it match the catalog.
- **Write roles, grants, policies or ownership.** A script that silently created roles and grants would be a privilege change disguised as a dump.
- **Restore a sequence's position.** A catalog records a sequence's declaration, not its current value, so a script-created sequence starts where its own `START WITH` puts it.
- **Write a sequence an identity column created.** The column's own declaration creates that sequence, so writing it as well would leave a second one behind, unused and renamed by the server to avoid the clash.
- **Load a foreign-key cycle unattended.** Each block is its own transaction, so the first block of a cycle needs rows a later block has not committed; `data.sql` names each cycle in a header comment, and the key validation at the end reports a cycle whose data truly disagrees.
- **Leave a binary value out.** A `bytea` value is inlined as `decode('<base64>', 'base64')::<type>`, which is why `data.sql` can be larger than the catalog's own data; the summary states how many bytes were inlined.

`summary.md` gains an **SQL scripts** section that names every file — the schema, then each data file — with its size, statement and row counts, the layout the store chose and why, the command that loads them, the guarantee and each of the limits above. The scripts are not part of the catalog's bytes and are not hashed: like the summary, deleting or editing them cannot break a catalog, and a restore never reads them.

!!! note "When nothing is written"
    The pass refuses rather than writing half a script — a catalog recorded by another engine's adapter, a catalog in the direct format, or an object the restore itself cannot create. The store still completes: the reason is one warning in the log and one sentence in the summary, and the catalog is exactly the catalog it would have been with the option off. Every file is built under a temporary name and all of them are moved into place only once the whole pass is complete — and a failure while moving them back leaves the scripts an earlier store wrote exactly as it found them.

## Viewing the relationships

The summary above states the relationships as text. DbClone also draws them as a picture, and it can draw it from either of two places:

- **A database you have open.** **View relationships** under the source panel draws the source, and the same button under the destination panel draws the target. Nothing has to be stored first.
- **A stored catalog.** The folder button next to **View relationships** under the source panel opens any catalog on disk, including one written in an earlier session. This path needs no connection at all, so a catalog can be explored on a machine that cannot reach the database it came from.

A finished store also draws the picture for you and writes it into the catalog folder beside `summary.md`, named after the folder, so a catalog explains itself as soon as it exists. Like the summary, that file is not part of the catalog's content: it is not listed in `manifest.json`, not hashed, not verified and never restored, so deleting it or editing it cannot break a catalog.

The picture itself — its cards and arrows, how they are arranged, how to narrow the view, and what it deliberately does not state — has its own page: [Relationship View](relationship-view.md).

## Restoring a catalog

1. Select the destination connection — it may name an existing database, or it may point only to the destination server if you want Restore to create a new database.
2. Press **Restore** (++ctrl+shift+r++).
3. In the folder browser, select the catalog folder **itself**, not the folder that contains it.
4. DbClone verifies the files and checks the selected target before applying the catalog. If the target contains user objects, it asks whether to update that database or create a new one.

| Result you see | Meaning |
|----------------|---------|
| **Restore Complete** (green) | The catalog was applied and the check after it found nothing to report; the banner gives the object count, the rows written and the elapsed time |
| **Restore Completed with Issues** (amber) | Objects were applied, and the restore recorded notes about what it did not carry, none of them an error — read them in the log |
| **Restore Completed with Issues** (red) naming counts | Every object was created, but the check after the data found tables holding a different number of rows than the catalog recorded. The banner states how many of the tables it checked disagree, and each difference is an error line in the log naming both numbers. The object count does not answer it: a table whose rows the target refused is still an object the run created, so the banner and the status bar state what the run completed with rather than what it created |
| **Update selected database** | After explicit confirmation, DbClone removes the selected database's user objects — schemas, tables and everything they carry, plus the extensions and publications that live beside a schema rather than in one — and restores the catalog into that same database. The check the restore itself runs is asked again once the cleaning finishes: a database that still holds what a restore refuses is not restored into, and the banner names what is left rather than only saying the target is not empty |
| **Create new database** | DbClone creates a new uniquely named database without replacing an existing database, then restores the catalog into it |
| **Restore cancelled** | No cleanup, database creation, or restore was started |
| **Restore refused** | The product's final empty-target safety check found user objects. The banner and the log name what it found — the extensions, tables or other objects the database still holds — so the reason is readable after the run. This can also happen if the target changed after the check that preceded the run; no restore objects are applied |
| **Restore FAILED** — "…object(s) this restore cannot create…" | The catalog holds a publication or a subscription. **Nothing was written** — the target is untouched, and the log names every such object |

!!! note "Restore safety"
    The product still refuses a populated target as a final backstop. The UI's **Update selected database** choice performs cleanup first and asks the same check again afterwards; the **Create new database** choice never drops or replaces an existing database. The question the UI asks names what the database holds, because it reads the target with the rule the restore enforces instead of with a count of its own.

## Current state of the restore

The restore keeps the product's refusal safety rule, while the UI offers an explicit choice before a populated target is changed. Today:

| Catalog content | On restore |
|-----------------|-----------|
| Schemas, extensions, custom types (enum, composite, domain), sequences, tables with their columns, defaults, generated and identity clauses, keys, constraints, partitions, functions, procedures, views, foreign keys, hand-made indexes, triggers | Applied, in dependency order — an extension is installed right after the schemas and before the types, so an object that names what it provides (an index over an operator class such as `gin_trgm_ops`, a column of an extension's type) is created instead of failing; an extension the catalog records in a schema the server owns, `plpgsql` in `pg_catalog`, is left to the target because every database it bootstrapped already holds it. A sequence an identity column created is not created on its own: the column's declaration creates it, so the identity owns exactly one sequence instead of leaving an unused one beside it. The trigger's own condition, column list and arguments come back from the statement the server reported, and a trigger the source had switched off is switched off again |
| Materialized views | The definition is created; the cached rows are not filled |
| Index that a primary key or unique constraint already made | Left alone — the constraint created it |
| Table rows | Written back in batches of at most 1 000 rows or about 2 MB of stored text, each value handed to the server in the type its source column had — so a column of a custom enum, domain or composite type comes back as that type instead of failing. A generated column is left out of the insert and the server computes it. Timestamps come back as the instants that were captured, whatever time zone the target server runs in. The tables are written in dependency order rather than in the order the catalog lists them — a table another table's foreign key points at comes before the table that points at it — so a table whose name sorts before its parent still receives its rows instead of being refused |
| What the restore checks after the data | It counts what the target now holds — schemas, tables, views, materialized views, sequences, functions, procedures, enums, domains, composite types and extensions — and reads the row count of every table whose data the catalog recorded. The sequences are expected where they are made: one for each the restore writes itself, and one for each identity column, since the column's own declaration creates that one. A table whose rows could not be written is still counted against the catalog's record of it, so the check reports it instead of finding the restore in agreement with itself. A difference is reported as an error naming both numbers — the banner states how many of the compared tables disagree — and a kind this check cannot count is named as unchecked rather than passed, so a green summary always stands for a check that ran |
| Indexes, triggers, constraints and column shapes | Not compared after the restore — the check counts objects and rows, not definitions. Nothing that failed to be created is quietly accepted: a step that fails is reported by name together with the objects it cost, and the steps after it still run, so one statement the server refuses never discards the rest of the restore. The two steps nothing else can be built on are the exception — a schema or table step that fails is reported as a failed restore, and the rows are left unwritten rather than attempted against tables that are not there |
| Publications and subscriptions | Refused before anything is written, by name. A subscription's statement is not even stored, because `CREATE SUBSCRIPTION` embeds the publisher's connection string and a catalog is a folder that gets copied — its name, the publication it reads, and a note about what was left out are recorded instead |
| Roles and grants | Not captured from the source yet, so not restored |
| Triggers while the rows are written | The structure is created before the data is applied, so a trigger the source had fires for the restored rows just as it would for any other insert — an audit trigger on the source writes its audit rows on the target as well |
| Sequence current values, the `OWNED BY` link of a sequence a serial column draws from, the `GENERATED ALWAYS` flavor of an identity column, table comments, tablespaces, fill factors, per-column statistics targets | Not captured — the catalog records the loss where it can, and the restore cannot recreate what it never held. The missing sequence position is the one you can hit: the rows come back with the ids the source gave them while the sequence starts at its beginning, so the first row inserted after a restore can collide with a restored one until the sequence is moved forward. The identity flavor is why those ids can be inserted at all — the restore writes `GENERATED BY DEFAULT`, which accepts an explicit value that a `GENERATED ALWAYS` column would have refused |

The preserved PostgreSQL-only statements are applied only when the target reports the same dialect the catalog was written from; a folder from another engine is refused instead of executed.

## Store & Restore options

**Store Options** on the toolbar holds:

| Setting | Effect |
|---------|--------|
| **Catalog Location** | Root folder for catalogs, with **Browse…**. The last folder you picked is used as the starting point |
| **Subfolder Pattern** | Folder name created under the root. `{Connection}` is the name of the saved connection selected on the Source panel — the source database name when none is selected — `{Database}` is the source database name, `{timestamp}` is `yyyy-MM-dd_HH-mm`. Spaces and any character a folder name cannot hold become `_`, a name Windows reserves as a device gets a `_` prefix, and an over-long name is cut while the timestamp tail is kept. Leave it empty to write straight into the root folder |
| **Include table data** | Off stores the schema only, and restoring such a catalog writes the structure and no rows. On stores the rows and the restore writes them back |
| **Write summary.md (statistics and relations)** | On writes the plain-text account described under [Reading the catalog summary](#reading-the-catalog-summary) into the catalog folder. Off stores the catalog alone — the folder is complete without it, and a restore needs neither file |
| **Write SQL scripts (schema and data)** | On writes the catalog's own SQL beside it — see [The catalog's own SQL](#the-catalogs-own-sql). Off stores the catalog alone; the folder is complete either way, and a restore never reads the scripts |
| **Largest data script file (MB), 0 = one file** | The largest `sql/data.sql` may become before the rows are written as a numbered family under `sql/data/` instead. `32` unless you change it; `0` writes one file whatever the catalog weighs. It changes only where the file boundary falls, never a statement |

These values live in `settings.json` under `StoreRestore`. Catalogs are currently stored as directories. ZIP packaging is a separate planned feature and is not currently exposed as a Store option.

| Setting | Default | Effect |
|---------|---------|--------|
| `ChunkBytes` | 8388608 (8 MiB) | Maximum byte size per chunk file; the catalog writer honours this limit when splitting table rows across chunk files |
| `RowsPerChunk` | 50000 | Maximum row count per chunk file; the 1 000 000-row Excel ceiling is enforced on top of this setting |
| `SnapshotConsistency` | `consistent` | The row read uses a consistent snapshot across all tables when set to `consistent`, or read-committed isolation when set to `readCommitted` |
| `UnsupportedFeaturePolicy` | `fail` | When `fail`, an unsupported object stops the restore before any write; when `warn`, the restore skips it and records an issue |
| `WriteSummaryMarkdown` | `true` | Whether Store writes `summary.md` into the catalog folder. It is an app preference: the setting decides, and nothing about the catalog itself changes |
| `SqlScripts` | `true` | Whether Store also writes the catalog's own SQL (`schema.sql`, then `data.sql` or a numbered family) beside it. On by default, so the files are there without opening the settings; turning it off leaves the catalog byte for byte as it was |
| `SqlScriptsMaxFileMb` | `32` | The largest a data script file may become, in megabytes, before the rows are written as a numbered family. `0` writes one file whatever the size. The application converts it to bytes once; it changes the file boundaries and no statement |

!!! note "Table selection narrows a catalog"
    A [table selection](connections/table-selection.md) is applied end-to-end: the catalog captures only the tables the selection includes, and the log names the excluded tables when Store starts.

## See also

- [Copy Modes](copy-modes/overview.md) — the live database-to-database route
- [Relationship View](relationship-view.md) — the picture of a catalog's or a live database's tables and keys
- [Options Reference](options.md) — copy, verify, and platform settings
- [The Main Window](getting-started/main-window.md) — where the panels and the toolbar are
- [Troubleshooting](troubleshooting.md) — connection and permission problems
