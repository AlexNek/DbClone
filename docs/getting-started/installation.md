# Installation

## Download

Download the latest installer from [GitHub Releases](https://github.com/AlexNek/DbClone/releases):

- **`DbClone-Setup-x.y.z.exe`** — standard installer with Start Menu shortcut

## Install

Run the installer and follow the prompts. No admin rights required — installs to your user profile by default.

!!! warning "Windows SmartScreen Warning"
    On first run you may see a **"Windows protected your PC"** dialog from Microsoft Defender SmartScreen. This happens because the installer is not code-signed with a purchased certificate — it does **not** mean the software is unsafe.

    To proceed:

    1. Click **"More info"**
    2. Click **"Run anyway"**

    This is standard behavior for open-source applications distributed outside the Microsoft Store. You can verify the file integrity using the SHA-256 checksum published on the [Releases](https://github.com/AlexNek/DbClone/releases) page.

![Installer Screenshot](../images/installer.png){ loading=lazy }

!!! info "No .NET runtime needed"
    DbClone ships self-contained. You don't need to install .NET separately.

## Silent Install

For deployment scripts or automation:

```batch
DbClone-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## Auto-Update

DbClone checks for updates automatically on startup (after a 3-second delay). When a new version is available, a dialog prompts you to download and install it.

Updates are applied in-place — no uninstall needed.

You can also check manually via **Help → About → Check for Updates**.

## Uninstall

Use Windows **Settings → Apps → DbClone → Uninstall**, or run the uninstaller from the install directory.

Your saved connections and settings are preserved in `%LOCALAPPDATA%\DbClone\` and are not removed on uninstall.
