# Resume Mode

Resume mode picks up a previously interrupted copy without re-creating schema objects.

## When to Use

- A Full copy was interrupted (network drop, timeout, cancellation)
- Schema was already created but data copy didn't finish
- You don't want to start over from scratch

## What Happens

1. **Skips DDL** — does not recreate schemas, types, tables, functions, etc.
2. **Compares row counts** — checks each table: source vs destination
3. **Copies only mismatched tables** — tables with fewer rows on destination get truncated and re-copied
4. **Tables with matching counts** are skipped entirely

## How It Decides What to Copy

For each table in the source:

| Source Rows | Dest Rows | Action |
|-------------|-----------|--------|
| 1000 | 1000 | ✅ Skip — already copied |
| 1000 | 500 | 🔄 Truncate + re-copy |
| 1000 | 0 | 🔄 Copy (table was empty) |

## Example Scenario

```
[10:30] Full copy started — 85 tables
[10:45] Network timeout after 60 tables
[10:46] User switches to Resume mode
[10:46] Resume: 60 tables match, 25 need copying
[10:55] Done — all 85 tables verified
```

## Tips

!!! tip "Don't modify the destination between runs"
    Resume mode trusts that the schema is correct. If you manually alter the destination schema between a failed Full copy and a Resume, you may get constraint errors.

- Resume is safe to run multiple times — it's idempotent
- If schema creation itself failed, use Full mode instead
