# FAQ

## General

### Does DbClone support MySQL, SQL Server, or other databases?

Not yet. DbClone currently supports PostgreSQL only. The architecture is designed for multi-provider support — adding MySQL or SQL Server would mean implementing the Application layer interfaces in a new project, but it's not on the immediate roadmap.

### Is DbClone free?

Yes — the **compiled binary** (installer/exe) is completely free for any use under the MIT License: personal, commercial, or otherwise. No restrictions, no source disclosure required.

The **source code** is governed by [PolyForm Shield 1.0.0](https://polyformproject.org/licenses/shield/1.0.0) with an open source exception:

- **Open source projects** (OSI-approved license, publicly available source) may use, modify, and contribute to the source — as long as the project is not a competing database copy/migration tool.
- **Commercial or competing use** of the source code requires a separate commercial license. Contact the author for details.

See [LICENSE.txt](https://github.com/AlexNek/DbClone/blob/main/LICENSE.txt) for the full terms.

### Does it work on macOS or Linux?

No. DbClone is a Windows desktop application (WPF). Cross-platform support would require a different UI framework.

---

## Connections

### Are my passwords stored securely?

Yes. Passwords are encrypted using Windows DPAPI (Data Protection API), which ties decryption to your Windows user account. They cannot be read by other users or if the disk is accessed from another machine.

### Can I use SSH tunnels?

DbClone doesn't have built-in SSH tunnel support. Use an external SSH tunnel (PuTTY, Windows OpenSSH) and connect to `localhost:local_port`.

### Does it work with PgBouncer / connection poolers?

Yes, but with caveats. DbClone uses features (COPY, temp tables) that require session-mode pooling. Transaction-mode poolers may cause issues. Use direct connections for best results.

---

## Copy Operations

### Which schemas does DbClone copy?

DbClone copies all schemas your connecting role can access. Schema handling has two layers:

| Layer | Behavior |
|-------|----------|
| System schemas | `pg_catalog`, `information_schema`, `pg_toast` are **never copied** — these are PostgreSQL internals managed by the server itself. DbClone checks they exist on the destination and attempts to repair `information_schema` if missing. Comparison **checks that they are present** on both sides, but their contents are not compared |
| Platform schemas | Controlled by the **"Platform Schemas"** checkbox in Copy Options (checked by default = included) |

### How does DbClone know which schemas are "platform schemas"?

DbClone uses **`.platform` definition files** — not hardcoded platform names. When you connect, DbClone:

1. **Auto-detects the platform** from the hostname (e.g. `*.supabase.co` → Supabase, `*.aivencloud.com` → Aiven)
2. **Resolves the server version** (e.g. PostgreSQL 15, 16, or 17)
3. **Looks up the matching version entry** in the `.platform` file to get the exact list of platform schemas and extensions

Currently supported platforms:

| Platform | Detection | Version-Specific Definitions |
|----------|-----------|------------------------------|
| **Supabase** | `*.supabase.co` | PG 15, 16, 17 (different extension sets per version) |
| **Aiven** | `*.aivencloud.com` | All versions (`aiven_extras`) |
| **Neon** | `*.neon.tech` | All versions (`neon`, `neon_utils`) |
| **Vanilla PostgreSQL** | Fallback | All versions (no platform schemas) |

When **"Platform Schemas" is checked** (default): everything is copied — including `auth`, `storage`, `realtime`, etc. This gives you a complete backup.

When **unchecked**: DbClone uses the platform definition to know which schemas to exclude, and also probes privileges to exclude any additional non-writable schemas.

**Typical usage:**
- **Backup mode**: leave checked → complete backup including platform schemas
- **Comparison against a clean DB**: uncheck → avoids noise from platform schemas that only exist in the backup
- **Copy to a live Supabase instance**: uncheck → destination already has its own platform schemas

### Can I add a custom platform or update the definitions?

Yes. The `.platform` files are JSON files stored in the `platforms/postgresql/` directory in the installation folder. You can edit them or add new ones without reinstalling DbClone. Each file defines:

- `detection.hostPatterns` — glob patterns to auto-detect the platform from the hostname
- `defaults` — default port and SSL mode
- `versions[]` — version-range entries with `systemSchemas`, `platformSchemas`, and `platformExtensions`

### Can I copy only specific tables?

DbClone copies all tables it has permission to access by default. Tables that fail during schema creation (e.g., due to missing extensions) are automatically skipped and excluded from the data copy stage. Manual table filtering (choosing which tables to include/exclude) is a possible future feature. See [Possible Future Features](roadmap.md) for this and other ideas.

### How long does a copy take?

Depends on database size and network speed. Rough benchmarks:

| Database Size | LAN | Internet (100 Mbps) |
|---------------|-----|---------------------|
| 100 MB | < 1 min | 1-2 min |
| 1 GB | 2-5 min | 5-10 min |
| 10 GB | 15-30 min | 30-60 min |
| 100 GB | 2-4 hours | 4-8 hours |

### Can I run DbClone while the source database is in use?

Yes. DbClone doesn't lock the source database. However, data modified during the copy may not be consistent — you'll get a snapshot in time per table, not a global consistent snapshot. For production copies, consider taking a pg_dump snapshot or using a read replica as the source.

### What happens if my network drops during a copy?

Use **Resume** mode to continue from where it left off. DbClone will compare row counts and only re-copy tables that are incomplete.

---

## Managed Platforms

### Does DbClone work with Supabase?

Yes — this is one of its primary use cases. DbClone handles Supabase-specific issues:

- **Auto-detects** the platform from `*.supabase.co` hostnames and resolves version-specific schemas (PG 15/16/17)
- Skips managed schemas (`auth`, `storage`, `realtime`, `supabase_migrations`, `graphql`, `vault`, etc.)
- Excludes extension-owned objects from copy and comparison (`supabase_vault`, `pgsodium`, `pg_graphql`, `pg_net`, etc.)
- Handles pooler connection requirements (default port 5432, SSL Require)
- Works with both direct and pooled connections

### What are "extension-owned objects" and why are they excluded?

Extensions (like Supabase's `realtime`, `auth`, or `pg_graphql`) create their own tables, functions, views, and types when installed. These objects are **managed by the extension** — not by you. PostgreSQL tracks ownership via its internal dependency catalog (`pg_depend`).

DbClone excludes extension-owned objects from both **copy** and **comparison** because:

1. **They already exist on the destination.** If both instances have the same extension installed, the extension created its own objects there independently.
2. **They may differ between versions.** Two Supabase instances on different platform versions will have slightly different definitions for the same extension tables (e.g., an extra `NOT NULL` constraint, or a `CHECK` added in a newer migration). These differences are expected and harmless.
3. **They cannot be reliably overwritten.** `CREATE TABLE IF NOT EXISTS` skips pre-existing tables, and `ALTER TABLE ... SET NOT NULL` fails if existing rows contain NULLs.

**What to do if you see differences on extension tables:**

If you run a comparison *outside* of DbClone (e.g., with `pg_dump` diffing) and notice differences on `realtime.*`, `auth.*`, or `graphql_public.*` tables, this is version drift between the two platform instances. To align them:

```sql
-- Update the extension on the destination to match the source version
ALTER EXTENSION realtime UPDATE;
```

DbClone's own comparison will not report these objects — they are filtered out at the model level.

### Does it work with Neon / Aiven / RDS?

Yes. Any PostgreSQL-compatible managed platform works. DbClone auto-detects capabilities and adjusts its behavior:

| Platform | Auto-Detection | Special Handling |
|----------|---------------|------------------|
| **Supabase** | `*.supabase.co` | Version-specific schemas/extensions for PG 15/16/17 |
| **Aiven** | `*.aivencloud.com` | Default port 11521, SSL Require, `aiven_extras` extension |
| **Neon** | `*.neon.tech` | `neon` and `neon_utils` extensions excluded |
| **Vanilla PostgreSQL** | Fallback | No platform schemas or extensions |
| **Other** (RDS, etc.) | Falls back to vanilla | Ownership probing for non-writable schemas |

### Why are some schemas excluded?

If you've unchecked **"Platform Schemas"** in Copy Options, DbClone excludes platform-managed schemas using the `.platform` definition files:

1. The hostname is matched against `detection.hostPatterns` to identify the platform
2. The server version selects the matching `versions[]` entry
3. The `platformSchemas` list from that entry is excluded

Additionally, DbClone probes `CREATE` privilege on every schema. Any schema where the current user lacks `CREATE` is excluded regardless of the platform definition — this catches custom service-role schemas that aren't in any definition file.

If **"Platform Schemas" is checked** (default), no filtering occurs — all accessible schemas are included.

---

## Errors & Troubleshooting

### "Extension X skipped" — is this a problem?

Usually not. Managed platforms restrict which extensions can be installed. If the extension is already present on your destination (or isn't needed), this is fine. Check the [troubleshooting guide](troubleshooting.md) for details.

### The copy "succeeded" but some tables are empty

Check the log for `FAIL` or `SKIPPED` entries. Common causes:

- Table depends on an unavailable extension
- Permission denied on the source table
- Table is partitioned and only the parent was counted

### Can I retry just the failed parts?

Yes — switch to **Resume** mode and run again. It will skip everything that already succeeded.
