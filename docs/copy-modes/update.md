# Update Mode

Update mode syncs changed tables from source to destination without touching unchanged data.

## When to Use

- Destination already has a complete copy and you want to refresh stale tables
- Periodic sync from production to staging
- After schema changes that only affected certain tables

## What Happens

1. **Skips DDL** — assumes schema is already correct on destination
2. **Compares row counts** for every table
3. **Truncates and re-copies** only tables where counts differ
4. **Leaves matching tables untouched**

## Difference from Resume

| | Resume | Update |
|--|--------|--------|
| Intent | Finish an interrupted copy | Refresh stale data |
| Schema assumption | Created by previous Full run | Already correct |
| Practical difference | Same behavior | Same behavior |

!!! note
    Resume and Update currently use the same logic. The distinction is semantic — Resume implies "I was interrupted" while Update implies "destination is stale". Future versions may add more intelligent diffing for Update mode.

## Limitations

- Does not detect schema changes (new columns, dropped tables)
- Does not handle row-level deletes — if source has fewer rows than destination, it won't remove the extras (use Full mode for that)
- For structural changes, run a Full copy instead
- Requires the "All Tables" selection — Update is blocked with an explanation while a non-default [table selection](../connections/table-selection.md) is active

## Tips

- Pair Update mode with **Checksum verification** to catch content differences beyond just row counts
- For tables with frequent updates but stable row counts, consider Full mode periodically
