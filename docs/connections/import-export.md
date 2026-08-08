# Import & Export Connections

## Back Up & Restore Everything (Recommended)

The fastest way to safeguard **all** your connections *and* groups is the one-click backup in the Connection Manager.

1. Open the Connection Manager
2. Click **Export All** (top-right, visible on every tab)
3. Choose a file location — DbClone suggests `DbClone-backup-YYYYMMDD.json`
4. *(Optional)* Enter a password to encrypt the backup
5. Click **OK**

To restore, click **Import All**, select your backup file, and enter the password if it was encrypted.

![Connection Manager](../images/connection-manager.png){ loading=lazy }

### Backup encryption

- Leave the password empty for a plain JSON backup (local use only).
- Enter a password to encrypt the file with **AES-256** (PBKDF2-SHA256 key derivation). Encrypted backups are safe to store or share.
- On import, DbClone detects encryption automatically and prompts for the password.

!!! warning "Keep your password safe"
    An encrypted backup **cannot be recovered** without its password. Store the password separately from the backup file.

!!! warning "Plain exports contain passwords"
    An unencrypted backup stores connection passwords in plain text. Handle such files securely and prefer encryption when sharing.

### What is included

A backup contains every connection (host, port, database, username, password, SSL mode, color, notes) and every group (name, source/destination links, color, notes). Importing merges with existing data — entries with the same ID are updated, new entries are added.

## Export a Single Connection

Export a connection string to share with team members or use in another tool.

1. Select a connection and click **Export**
2. Choose a format — Npgsql/.NET, PostgreSQL URI, libpq/psql, JDBC, SQLAlchemy (Python), Prisma, Node.js (pg), Supabase URI, Supabase (env), or Environment Variable
3. Optionally tick **Set as default for clipboard** — the quick **Export to Clipboard** action (connection list menu) uses this format
4. Choose the output:
    - **Copy to Clipboard**
    - **Save to File** (with **Browse...** to pick a location)
5. Click **Export** — a live preview shows exactly what will be exported

!!! warning "Passwords in exports"
    Exported connection strings include the password in plain text. Handle exported files securely.

## Import a Single Connection

Import a connection from a pasted connection string.

1. Click **Import** in the Connection Manager
2. Paste your connection string — the format is detected automatically (or click **Detect Format**)
3. Review the parsed values preview (host, port, database, user, SSL mode)
4. Click **Import** — the connection is added as a new entry

![Import Connection](../images/import-connection.png){ loading=lazy }

## Supported Import Formats

| Format | Example |
|--------|---------|
| PostgreSQL URI | `postgres://user:pass@host:5432/db` |
| Npgsql / .NET key-value | `Host=host;Port=5432;Database=db;Username=user;Password=pass` |
| libpq / psql | `host=host port=5432 dbname=db user=user password=pass` |
| JDBC | `jdbc:postgresql://host:5432/db?user=user&password=pass` |
| Node.js (pg) | `pg://user:pass@host:5432/db` |
| Supabase URI / Supabase (env) | Supabase connection strings and environment snippets |
| Environment Variable | `DATABASE_URL=postgres://user:pass@host:5432/db` |

!!! note "Export-only formats"
    SQLAlchemy (Python) and Prisma formats are available when **exporting** a connection, but cannot be imported.

## Bulk Operations

To move multiple connections at once, use **Export All** / **Import All** (see the backup section above). The backup file is a JSON document containing all connections and groups:

```json
{
  "Connections": [ ... ],
  "Groups": [ ... ],
  "ExportedAt": "2026-01-15T10:30:00",
  "Version": 1
}
```
