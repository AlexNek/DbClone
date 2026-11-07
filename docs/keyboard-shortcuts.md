# Keyboard Shortcuts

DbClone supports the following keyboard shortcuts. They work in either mode (Copy or Compare).

## Operations

| Shortcut | Action | Notes |
|---|---|---|
| ++ctrl+s++ | **Start Copy** | Begins the copy operation using current settings |
| ++ctrl+shift+c++ | **Run Compare** | Starts database comparison (auto-switches to Compare mode) |
| ++ctrl+shift+s++ | **Start Store** | Writes the source connection's database into a catalog folder ([Store & Restore](store-restore.md)) |
| ++ctrl+shift+r++ | **Start Restore** | Applies a catalog folder to the destination connection ([Store & Restore](store-restore.md)) |
| ++ctrl+p++ | **Pause / Resume** | Toggles pause on whichever operation is running |
| ++escape++ | **Stop** | Cancels the active operation |

## Utility

| Shortcut | Action | Notes |
|---|---|---|
| ++f1++ | **Open Documentation** | Opens the online user manual in your default browser |
| ++ctrl+l++ | **Clear Log** | Clears all entries in the log pane |

!!! tip
    ++ctrl+s++ and ++ctrl+shift+c++ trigger their respective operations directly — you don't need to switch modes first; the UI switches to the matching mode when the operation starts. ++ctrl+shift+s++ and ++ctrl+shift+r++ do not change the mode: Store and Restore are operations on the connection panels, not a mode.
