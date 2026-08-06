# Connection Formats

DbClone supports multiple connection string formats. You can paste any of these into the connection manager.

## URI Format

The standard PostgreSQL connection URI:

```
postgres://username:password@hostname:5432/database_name?sslmode=require
```

### Special Characters in Passwords

Passwords with special characters must be URL-encoded in URI format:

| Character | Encoded |
|-----------|---------|
| `@` | `%40` |
| `#` | `%23` |
| `%` | `%25` |
| `/` | `%2F` |
| `:` | `%3A` |
| space | `%20` |

Example with special password `p@ss#word`:

```
postgres://user:p%40ss%23word@host:5432/mydb
```

!!! tip
    If you fill in the individual fields instead of pasting a URI, DbClone handles encoding automatically.

## Key-Value Format

The standard Npgsql/ADO.NET connection string format:

```
Host=hostname;Port=5432;Database=mydb;Username=user;Password=p@ss#word;SSL Mode=Require
```

No encoding needed — semicolons delimit fields, and the password is taken as-is.

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
