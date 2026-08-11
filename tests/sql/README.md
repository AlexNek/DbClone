# Manual Test SQL Fixtures

Hand-run SQL scripts used to prepare **source** and **destination** databases
for manual testing of DbClone. They are NOT executed by the automated test
suite (tests must stay hermetic and never touch a live database).

Run them with any SQL client against a throwaway database. All scripts are
**idempotent** — re-running them always returns to the same state.

> Use only disposable databases for testing. Connection examples in this
> document use placeholder hosts such as `test.example.com`.

| File | Purpose | Min. version |
|------|---------|--------------|
| `test0.sql` | Minimal extension bootstrap (run when `test.sql` is overkill, e.g. permission probes) | PG 16 |
| `test.sql` | Comprehensive copy test suite — every data type, partitioning, views, triggers, RLS, replication | PG 16 baseline; some tables need PG 17 / PG 18 destinations |
| `test-table-selection.sql` | Dependency-rich fixture for the **table selection** feature — FKs, views, partitions, wrong-use-case scenarios | PG 16 |

---

## test0.sql

Creates the four most commonly required extensions (`pgcrypto`,
`btree_gist`, `btree_gin`, `pg_trgm`). Use it to verify extension-creation
permissions before a full run.

## test.sql

Comprehensive database copy stress test. Everything lives in schema
`copy_test` (dropped and recreated on every run); the publication
`test_pub` is cluster-level and dropped explicitly.

### Section map

| § | Content | What it verifies in DbClone |
|---|---------|------------------------------|
| 0–1 | Teardown, extensions | Idempotent re-run; extension copy |
| 2 | Enums (`order_status`, `priority_level`) incl. `ALTER TYPE … ADD VALUE` | Type dependency ordering, enum evolution |
| 3 | Domains with CHECK constraints | Domain DDL replication |
| 4 | **PG 17+** table: `uuidv7()`, `vector` + HNSW index, `jsonpath`, `MERGE … RETURNING` demo tables | Version-gated features; destination must be PG 17+ |
| 5 | **PG 18+** table with a `VIRTUAL` generated column | Destination must be PG 18+ |
| 6 | `test_all_types` — ~45 columns covering every PG 16 data type | Binary COPY serialization stress test |
| 7 | `test_enum_domain` — enum/domain columns with defaults | Enum + domain column copy |
| 8 | Range partitioning (composite key) + hash partitioning + DEFAULT partitions | Partition metadata, bounds, `PARTITION OF` DDL |
| 9 | Sequences: standalone + identity (`GENERATED ALWAYS`) + custom default sequence | Sequence value sync (`setval`) |
| 10 | All index kinds: btree, hash, GIN, GiST, BRIN, SP-GiST, covering, partial, expression, trigram | Index DDL replication |
| 11 | View + materialized view (with unique index) | View dependency ordering |
| 12 | RLS: `ENABLE/FORCE ROW LEVEL SECURITY` + policy | Policy copy |
| 13 | Functions + triggers on two tables | Function copy, trigger reattachment |
| 14 | Logical replication publication | Publication copy |
| 15 | Data: edge values (`infinity` dates, max numerics, empty strings, NULLs), 5000 range-partition rows, 2000 hash-partition rows | Row-level fidelity, Compare counts |

### Version layering

| Table | Required destination |
|-------|---------------------|
| `test_all_types` and all others | PG 16+ |
| `test_pg17_types`, `merge_target/source`, `test_json_source` | PG 17+ |
| `test_pg18_types` | PG 18+ |

Copying into an older destination is expected to fail on those tables only —
a useful negative test for stage error reporting.

## test-table-selection.sql

Fixture for the **manual table selection** feature. Everything lives in
schema `sel_test`. Row counts are deterministic so Compare results are
reproducible.

### Object map

| Object | Dependency role |
|--------|-----------------|
| `customers` ← `orders` ← `order_items` → `products` | FK chain — exclude any node and watch dangling-FK handling |
| `audit_log` → `customers` | "Bystander" FK — usually left unselected; triggers the **abort-before-destruct** boundary check |
| `employees` → `employees` | Self-referencing FK |
| `tbl_a` ↔ `tbl_b` | Circular deferred FKs |
| `events` + `events_y2024/y2025/default` | Range partitioning — parent vs single-partition exclusion |
| `"MixedCase"` / `"mixedcase"` | Case-insensitive `TableId` matching probe |
| `users` + RLS policy | Table-owned policy |
| `seq_probe` (identity) + `standalone_sel_seq` | Owned vs standalone sequences |
| `v_order_totals`, `v_customer_orders`, `v_big_orders` (view-on-view), `mv_product_stats` | Direct, multi-table, transitive and materialized view dependencies |
| `fn_sel_touch()` + triggers on `customers`, `orders` | Table-owned triggers |

### Scenarios (also embedded at the end of the script)

| ID | Action | Expected behavior |
|----|--------|-------------------|
| A | Exclude `orders` | Dangling FK on `order_items` stripped; `v_order_totals`, `v_customer_orders` **and transitive `v_big_orders`** skipped; trigger goes with the table |
| B | Exclude partition parent `events` | All three partitions reported as orphaned/skipped |
| C | Exclude only `events_y2024` | **Wrong use case** — scoped clean must abort before any DROP (partition boundary conflict) |
| D | Select only `customers`, destination primed with this script | **Wrong use case** — `audit_log`'s FK into `customers` aborts the scoped clean; "clear entire destination" choice must succeed and leave only `customers` |
| E | Save preset, drop `legacy_notes` on source, re-apply | Stale exclusion reported, copy proceeds |
| F | Exclude `"MixedCase"` | Case-insensitive matching excludes **both** case variants |
| G | Exclude only `tbl_a` | Dangling `tbl_b.fk_b_to_a` stripped, no deadlock |
| H | Exclude `users` | RLS policy disappears with the table |
| I | Exclude `seq_probe` | Identity sequence goes; `standalone_sel_seq` survives |

### Typical two-database setup

1. Source DB (e.g. `srctest`): run `test-table-selection.sql`.
2. Destination DB (e.g. `dsttest`):
   - leave **empty** for plain copy scenarios, or
   - run the same script to prime scenarios C/D (existing tables + FKs).
3. In DbClone: pick the source connection, open the table selection dialog,
   apply a scenario, start a Full copy, and choose between
   *replace only the selected tables* / *clear entire destination* when the
   cleanup dialog appears.

## Notes

- Do not point these scripts at production databases — teardown drops the
  whole fixture schema (`copy_test` / `sel_test`) with `CASCADE`.
- Scenario F reflects a deliberate design decision: table identity matching
  is case-insensitive, mirroring how users type names into presets.
