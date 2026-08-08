# Troubleshooting

## Common Issues

### Connection refused / timeout

**Symptoms:** "Cannot connect to source/destination" error during Connect stage.

**Causes:**

- Firewall blocking port 5432 (or custom port)
- Server not accepting remote connections (`listen_addresses` in `postgresql.conf`)
- Managed platform requires SSL (`sslmode=require`)

**Solutions:**

1. Verify you can connect with `psql` or pgAdmin first
2. Check SSL mode — set to **Require** for cloud platforms
3. Whitelist your IP in the platform's firewall settings
4. Check that the port is correct (Supabase pooler uses 6543, direct uses 5432)

---

### Permission denied on schema

**Symptoms:** "Schema 'auth' excluded — no CREATE privilege" warning.

**This is expected.** Managed platforms (Supabase, Neon) have internal schemas that you can't write to. DbClone detects this and skips them automatically.

If you see this for schemas you *do* own, check that your user has `CREATE` privilege:

```sql
GRANT CREATE ON SCHEMA my_schema TO my_user;
```

---

### Extension creation failed

**Symptoms:** "Extension pgcrypto skipped: permission denied to create extension"

**Cause:** Many managed platforms restrict which extensions can be installed. Only the platform's allowed list works.

**What DbClone does:** Skips the extension and continues. Tables that depend on extension-provided types (e.g., `uuid` from `uuid-ossp`) will still work if the extension already exists on the destination.

**Solution:** Pre-install required extensions on the destination manually, or ensure they're in the platform's allowed list.

---

### Binary COPY failed / data type mismatch

**Symptoms:** "COPY failed for table X" error during CopyData stage.

**Causes:**

- Source and destination have different PostgreSQL major versions with incompatible binary formats
- Custom types that don't transfer cleanly

**Solutions:**

1. DbClone automatically falls back to INSERT mode for failed tables
2. Check that the destination has the same custom types/extensions
3. Ensure both servers are on compatible PostgreSQL versions

---

### Connection dropped during long copy

**Symptoms:** "Connection validation failed, forcing reopen" in the log, or complete failure on a large table.

**Causes:**

- Cloud proxy timeout (Supabase/PgBouncer idle timeout is often 60-300 seconds)
- Network interruption

**Solutions:**

1. DbClone sends keepalive heartbeats between stages to prevent idle drops
2. For very large tables, the binary COPY stream keeps the connection active
3. If it still fails, use **Resume** mode to pick up where it left off
4. Consider using direct connections instead of pooled connections for large copies

---

### "Table X skipped: requires extension Y"

**Symptoms:** Warning during CreateTables stage.

**Cause:** A table has a column type provided by an extension that couldn't be installed on the destination.

**What to do:**

1. Install the extension manually on the destination if possible
2. Or accept that this table can't be copied to this destination
3. The data copy stage will skip these tables automatically

---

### DDL differences on `realtime.*` or `auth.*` tables after a fresh copy

**Symptoms:** External comparison tools (or an older DbClone version) report differences like:

- `DDL differs: CHECK modified: messages_payload_exclusive (source: ... NOT VALID, dest: ...)`
- `DDL differs: Column "claims_role": nullable (was NOT NULL)`

**Cause:** These tables are **extension-owned** — they were created by the `realtime` or `auth` extension independently on both the source and destination Supabase instances. When the two instances run different platform versions, the extension's internal migrations produce slightly different definitions:

- A `CHECK` constraint added via `ALTER TABLE ... NOT VALID` on one instance (skips row validation) vs. created normally on the other
- A column changed to `NOT NULL` in a newer extension version, while the older instance still allows NULLs

**This is not a copy error.** DbClone (current version) excludes extension-owned objects from both copy and comparison, so you should not see these differences in DbClone's report. If you see them in an external tool:

1. **Ignore them** — they are cosmetic version drift, not data loss
2. **Or align extension versions:**
   ```sql
   -- On the destination, update to match the source
   ALTER EXTENSION realtime UPDATE;
   ALTER EXTENSION auth UPDATE;
   ```
3. For the `NOT NULL` case, backfill NULLs first if the update fails:
   ```sql
   UPDATE realtime.subscription SET claims_role = '' WHERE claims_role IS NULL;
   ALTER TABLE realtime.subscription ALTER COLUMN claims_role SET NOT NULL;
   ```

---

### information_schema missing in destination

**Symptoms:**

- Comparison row: `Schema | information_schema | Missing in Dest | System schema missing in destination`
- Copy warning: `Could not restore information_schema via server-side script — install script not readable via pg_read_file (requires superuser or pg_read_server_files role)`

**Cause:** Someone dropped `information_schema` on the destination database. DbClone never copies system schemas (PostgreSQL builds them itself), but it tries to reinstall a missing `information_schema` using the server's own install script via `pg_read_file` — which needs superuser or the `pg_read_server_files` role. Without that, DbClone only creates an empty placeholder schema and warns.

**Solution A — recreate the destination database (simplest):**

```sql
-- connected to another database (e.g. postgres), as superuser
DROP DATABASE yourdb;

-- create a dedicated admin role for this database only (skip if it already exists)
CREATE ROLE yourdb_admin LOGIN PASSWORD 'change-me';

CREATE DATABASE yourdb OWNER yourdb_admin;
```

> **Note:** `CREATE ROLE ... LOGIN` is identical to `CREATE USER` — since PostgreSQL 8.1 users are just roles with the `LOGIN` attribute; `CREATE USER` implies `LOGIN` by default. No `GRANT` needed: the owner automatically has all database-level privileges (`CONNECT`, `CREATE`, `TEMP`).

A freshly created database gets `information_schema` automatically, and the owner role controls all objects in it. On PostgreSQL 14 or older, also fix the `public` schema owner:

```sql
-- connected to yourdb
ALTER SCHEMA public OWNER TO yourdb_admin;
```

Use `yourdb_admin` as the destination connection in DbClone and re-run the copy in **Full** mode.

**Solution B — restore in place with the install script:**

```sql
-- 1. Connect to the affected database as superuser, find the share directory
SELECT setting FROM pg_config WHERE name = 'SHAREDIR';

-- 2. Drop the empty placeholder first — the install script uses
--    CREATE SCHEMA without IF NOT EXISTS and would fail otherwise
DROP SCHEMA IF EXISTS information_schema CASCADE;

-- 3. Run the install script from the share directory (psql):
\i 'C:/Program Files/PostgreSQL/17/share/information_schema.sql'

-- 4. Verify
SELECT count(*) FROM information_schema.tables;
```

**If it keeps happening in new databases**, `information_schema` was probably dropped from `template1`, so every new database inherits the problem:

```sql
-- connected to template1
SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'information_schema');
```

If `false`, apply Solution B to `template1` as well.

---

## Log Files

DbClone writes detailed logs to a `logs` folder in your roaming profile:

```
%APPDATA%\DbClone\logs\
```

Typical path: `C:\Users\{you}\AppData\Roaming\DbClone\logs\`

(When running a Debug build from the source tree, logs are written next to the executable instead.)

Log files are rotated daily. Each contains:

- Full pipeline execution trace
- SQL statements executed (in Debug mode)
- Error stack traces
- Connection state changes

### Crash Log

If DbClone crashes on startup, a `crash.log` file is written to your local profile:

```
C:\Users\{you}\AppData\Local\DbClone\crash.log
```

---

## Getting Help

If you encounter an issue not covered here:

1. Check the log file for detailed error messages
2. [Open an issue](https://github.com/AlexNek/DbClone/issues) on GitHub with:
    - DbClone version
    - Source/destination PostgreSQL versions
    - Source platform (Supabase, Aiven, vanilla, etc.)
    - Relevant log excerpts (redact credentials!)
