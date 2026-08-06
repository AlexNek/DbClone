# Copy Modes

DbClone offers four copy modes to match different scenarios:

| Mode | Use Case | Destination State |
|------|----------|-------------------|
| [**Full**](full.md) | Fresh clone | Empty (existing data will be cleaned) |
| [**Resume**](resume.md) | Interrupted copy | Partially copied (schema exists) |
| [**Update**](update.md) | Sync changed tables | Already contains data |
| [**Backup**](backup.md) | Point-in-time backup | New database created automatically |

![Copy Mode Selector](../images/copy-mode-selector.png){ loading=lazy }

## Choosing the Right Mode

```mermaid
graph TD
    A[What do you want to do?] --> B{Fresh clone, drop everything on destination?}
    B -->|Yes| C[Use Full]
    B -->|No| D{Continuing an interrupted copy?}
    D -->|Yes| E[Use Resume]
    D -->|No| F{Refresh only changed tables?}
    F -->|Yes| G[Use Update]
    F -->|No| H{Create a separate timestamped copy?}
    H -->|Yes| I[Use Backup]
    H -->|No| C
```

## Common to All Modes

Regardless of mode, DbClone always:

- Detects extension-owned objects and skips them
- Resolves dependency ordering
- Reports progress per table
- Isolates errors (one table failing doesn't stop the rest)
- Runs validation at the end
