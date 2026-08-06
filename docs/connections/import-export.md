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

Export a connection to share with team members or transfer to another machine.

1. Right-click a connection → **Export**
2. Choose output format:
    - **URI** — standard `postgres://` format
    - **Key-Value** — `Host=...;Port=...` format
    - **JSON** — structured format with all fields
3. Choose destination:
    - **Clipboard** — copies to clipboard
    - **File** — saves to a `.json` or `.txt` file

!!! warning "Passwords in exports"
    Exported connections include the password in plain text. Handle exported files securely.

## Import a Single Connection

Import connections from a file or clipboard.

1. Click **Import** in the Connection Manager
2. Paste or browse to your connection data
3. DbClone auto-detects the format (URI, key-value, or JSON)
4. Choose whether to create a new connection or overwrite an existing one
5. Click **Import**

![Import Connection](../images/import-connection.png){ loading=lazy }

## Supported Import Formats

| Format | Example |
|--------|---------|
| URI | `postgres://user:pass@host:5432/db` |
| Key-Value | `Host=host;Port=5432;Database=db;Username=user;Password=pass` |
| JSON | `{"host": "host", "port": 5432, ...}` |

## Bulk Operations

You can import multiple connections at once from a JSON array:

```json
[
  {"name": "Dev", "host": "localhost", "port": 5432, "database": "app_dev", "username": "dev"},
  {"name": "Staging", "host": "staging.example.com", "port": 5432, "database": "app", "username": "deployer"}
]
```
