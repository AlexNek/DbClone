# Connection Formats

DbClone supports multiple connection string formats. You can import any of these through the Connection Manager (**Import from Clipboard** or the **Import** button).

## URI Format

The standard PostgreSQL connection URI:

```
postgres://username:password@hostname:5432/database_name?sslmode=require
```

### Special Characters in Passwords

DbClone handles special characters in passwords automatically — you can paste a URI with an unencoded password and it will be parsed correctly.

The typical workflow: copy the URI template from your provider's dashboard (which usually contains a `[YOUR-PASSWORD]` placeholder), replace the placeholder with your real password, and paste the result. Your password goes in raw — no percent-encoding needed:

```
postgres://user:p@ss#word@host:5432/mydb
```

The parser uses a tolerant approach: it scores every possible `@` split (preferring a real-looking host — dotted name, port, database path — over a bare word) and picks the best reading, so the password is identified correctly even when it contains `@`, `#`, `&`, `?`, `:`, `/`, or spaces. Special characters in query-parameter values are tolerated too:

```
postgres://user:p@ss@host:5432/mydb?application_name=my@app
```

Pre-encoded URIs (as produced by most cloud dashboards) also work:

```
postgres://user:p%40ss%23word@host:5432/mydb
```

Both forms import to the same result — password field shows `p@ss#word`.

Common percent-encoding reference:

| Character | Encoded |
|-----------|---------|
| `@` | `%40` |
| `#` | `%23` |
| `%` | `%25` |
| `/` | `%2F` |
| `:` | `%3A` |
| `&` | `%26` |
| `?` | `%3F` |
| space | `%20` |

!!! tip
    If you fill in the individual fields instead of pasting a URI, DbClone handles encoding automatically on export.

!!! note
    If both your password and a query-parameter value contain `@` **and** the host has no port, path, or dots, parsing is inherently ambiguous — percent-encode the password (`%40`) to remove any doubt.

## Key-Value Format

The standard Npgsql/ADO.NET connection string format:

```
Host=hostname;Port=5432;Database=mydb;Username=user;Password=p@ss#word;SSL Mode=Require
```

No encoding needed — semicolons delimit fields, and the password is taken as-is.

## What the Import Fills In and Reports

An incomplete string is still importable — **Import** works for any text a recognised format understands, not only a complete one. Where the text omits something the connection needs, DbClone takes the value from the engine's own platform data (the same source that fills the **Connection Type** defaults) and **tells you which value it assumed** instead of filling it in silently:

```
Port is not specified; the default 5432 will be used.
```

This is the common case with managed providers: the string a Neon or Supabase dashboard publishes often carries no port at all.

Every assumption and every oddity is listed as a note under the preview in the **Import Connection** dialog, and as a status line when you import a backup in the Connection Manager. The notes do not block the import — the values land in the form and you can change any of them before saving.

| What you see | What happened |
|--------------|-------------|
| `Port is not specified; the default 5432 will be used.` | The text carried no port; the engine's default was taken from its platform data |
| `Port 99159 is outside the range 1-65535.` | A port the text **did** supply, kept exactly as written and flagged — nothing is silently corrected, you decide what to do with it |
| `Unrecognized parameter ignored: sslmode2` | A key no format understands was dropped rather than passed through to the driver |
| `Password is missing.` | The text named no password |
| `Database name is not specified.` | No database in the text — fine for a server-only connection (Backup, Restore into a new database, Table Overview), see [Managing Connections](managing-connections.md) |
| `SSL is disabled.` | The text asked for `sslmode=disable` |

!!! note
    A text no registered format recognises is reported as such — there is no guess-and-save fallback. Percent-encoded values are decoded once, so a pre-encoded URI and a raw one produce the same fields.

## Platform-Specific URIs

DbClone recognizes connection strings from popular managed platforms:

### Supabase

```
postgres://postgres.[project-ref]:[password]@aws-0-[region].pooler.supabase.com:6543/postgres
```

### Neon

```
postgres://[user]:[password]@[endpoint].neon.tech/[database]?sslmode=require
```

### Aiven

```
postgres://[user]:[password]@[service]-[project].aivencloud.com:12345/defaultdb?sslmode=require
```

### AWS RDS

```
postgres://[user]:[password]@[instance].[region].rds.amazonaws.com:5432/[database]
```

All of these are parsed as standard PostgreSQL URIs — no special handling needed.
