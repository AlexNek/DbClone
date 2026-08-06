# Validation & Re-Copy

After copying data, DbClone validates that the destination matches the source using two levels of verification.

## Level 1: Data Validation

Verifies that table data was copied correctly. Choose a verification mode in the options panel:

| Mode | Speed | Accuracy | Method |
|------|-------|----------|--------|
| **Row Count** | ⚡ Fast | Good | Compares `COUNT(*)` per table |
| **Checksum** | 🐢 Medium | High | Compares MD5 hash of all row data |
| **Full** | 🐌 Slow | Highest | Row count + content checksum |

### Row Count (default)

Runs `SELECT COUNT(*) FROM table` on both source and destination. Fast but won't detect corrupted or modified rows.

### Checksum

Computes `MD5(string_agg(row::text))` for each table. Catches content differences but requires a full table scan on both sides.

### Full

Combines Row Count and Checksum. First checks counts (fast rejection), then verifies content for tables that pass the count check.

### Data Validation Results

After validation, each table gets a status:

- **OK** — matches source
- **MISMATCH** — row count or content differs
- **SKIPPED** — table wasn't created on destination (see warnings)
- **ERROR** — couldn't validate (connection issue, permission, etc.)

Results appear in the log panel:

```
[14:32:00] OK: public.users (1,234 rows)
[14:32:00] OK: public.orders (56,789 rows)
[14:32:01] MISMATCH: public.sessions (source=100, dest=98)
```

## Level 2: Object Count Validation

After data validation, DbClone verifies that the expected number of schema objects exist on the destination. This catches silent omissions — cases where a stage didn't run or an object type wasn't handled.

| Object Type | What is checked |
|-------------|----------------|
| **Tables** | Count of user tables (excluding system schemas and skipped tables) |
| **Indexes** | Count of secondary indexes (or only primary key indexes if `CopyIndexes` is off) |
| **Views** | Count of regular views (if `CopyViews` is enabled) |
| **Materialized Views** | Count of materialized views (if `CopyMaterializedViews` is enabled) |
| **Sequences** | Count of standalone/serial sequences excluding identity backing sequences (if `CopySequences` is enabled) |
| **Functions** | Count of functions/procedures (if `CopyFunctions` is enabled) |
| **Triggers** | Count of non-internal triggers (if `CopyTriggers` is enabled) |

For sequences and functions, the validation only flags if the destination has **fewer** than expected — the destination may legitimately have extra identity-backing sequences or system functions.

If a count mismatch is detected, it appears in the log as an infrastructure warning:

```
[14:32:05] Indexes: expected 42, found 38
[14:32:05] Views: expected 10, found 9
```

## Automatic Re-Copy

When mismatches are detected, the **ReCopyMismatched** stage runs automatically:

1. Truncates the mismatched table on destination
2. Re-copies all data from source
3. Validates again

This handles transient issues (network blips during COPY, timeout on a single table).

!!! info "Re-copy runs once"
    If a table still mismatches after re-copy, it's reported as a persistent error. DbClone won't retry indefinitely.

## Skipping Validation

If you don't need validation (e.g., very large databases, development environments), you can deselect the **Copy Data** checkbox to perform a schema-only clone. This skips data validation but still runs object count validation to verify schema completeness.
