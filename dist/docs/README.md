# rsyncWindows

A ground-up, protocol-compatible reimplementation of `rsync` for Windows: a client
(`rsyncWindows.exe`), a daemon that runs as a Windows Service, and a tray app for managing that
service. It speaks real rsync's wire protocol (protocol version 32, the same one rsync 3.x
uses), so it interoperates with genuine `rsync` installations on Linux/macOS as well as with
other rsyncWindows installs.

This document covers installation and day-to-day usage of both the client and the server. If
you're reading this from inside the `dist` folder, the installer (`install.ps1`) is one level up.

Copyright 2026 Jose Rodriguez Arroyo. Licensed under the Apache License, Version 2.0 -- see
`LICENSE` in this folder (or the repository root) for the full text.

---

## Contents

- [Installing](#installing)
- [Client usage](#client-usage)
  - [Basic syntax](#basic-syntax)
  - [Local copy](#local-copy)
  - [Talking to a real Linux/macOS rsync over SSH](#talking-to-a-real-linuxmacos-rsync-over-ssh)
  - [Talking to another Windows machine over SSH](#talking-to-another-windows-machine-over-ssh)
  - [Talking to an rsync daemon (`rsync://`)](#talking-to-an-rsync-daemon-rsync)
  - [Supported options](#supported-options)
- [Server usage (the daemon / Windows Service)](#server-usage-the-daemon--windows-service)
  - [The config file](#the-config-file)
  - [Module options reference](#module-options-reference)
  - [Managing the service](#managing-the-service)
  - [The tray app](#the-tray-app)
  - [Firewall](#firewall)
- [Interoperability notes](#interoperability-notes)
- [Troubleshooting](#troubleshooting)
- [Uninstalling](#uninstalling)

---

## Installing

From an **elevated** ("Run as Administrator") PowerShell prompt, from inside this `dist` folder:

```powershell
.\install.ps1
```

This copies the client, the daemon service, and the tray app to
`C:\Program Files\rsyncWindows\{cli,service,tray}`, registers and starts the daemon as a Windows
Service named **rsyncWindows**, opens an inbound firewall rule for port 873 (the daemon's
default port), and adds the client folder to the machine-wide `PATH` so `rsyncWindows` can be run
from any terminal, by any user, without typing a full path.

Useful options:

```powershell
.\install.ps1 -InstallDir "D:\Tools\rsyncWindows"   # install somewhere else
.\install.ps1 -NoService                            # client only, don't install the daemon
.\install.ps1 -NoFirewallRule                        # don't touch the firewall
.\install.ps1 -NoPathChange                          # don't touch PATH
.\install.ps1 -Port 8730                             # firewall rule for a non-default port
```

After installing, **open a new terminal window** (PATH changes don't apply to already-open
shells) and confirm it worked:

```powershell
rsyncWindows --version
```

See [Uninstalling](#uninstalling) at the bottom for `uninstall.ps1`.

---

## Client usage

### Basic syntax

```
rsyncWindows [OPTION]... SRC DEST
```

Exactly one of `SRC`/`DEST` may be remote; the other must be local. A `SRC`/`DEST` argument is
classified the same way real rsync classifies it:

| Form | Meaning |
|---|---|
| `C:\path\to\thing` or `.\relative\path` | Local |
| `user@host:/path` or `host:/path` | Remote over SSH |
| `host::module/path` | Remote daemon (rsync protocol directly, default port 873) |
| `rsync://host[:port]/module/path` | Remote daemon (explicit URL form) |

A trailing slash on a **directory** source changes what gets copied, matching real rsync exactly:

```powershell
rsyncWindows -av C:\src\stuff  D:\dest\      # copies "stuff" itself -> D:\dest\stuff\...
rsyncWindows -av C:\src\stuff\ D:\dest\      # copies stuff's CONTENTS -> D:\dest\...
```

### Local copy

```powershell
rsyncWindows -av C:\Users\me\Documents\ D:\Backup\Documents\
```

`-a` (archive) preserves timestamps, permissions (best-effort, see below), and recurses into
subdirectories; `-v` prints each file's path as its transfer begins (not just a summary at the
end), matching real rsync's own `-v` behavior.

### Talking to a real Linux/macOS rsync over SSH

Works exactly like real rsync's SSH mode, using Windows 10+'s built-in OpenSSH client:

```powershell
# push local -> remote Linux box
rsyncWindows -avz C:\Projects\myapp\  myuser@192.168.1.50:/home/myuser/myapp/

# pull remote -> local
rsyncWindows -avz myuser@192.168.1.50:/home/myuser/myapp/  C:\Projects\myapp\
```

- `-z` enables compression over the wire (real zlib underneath -- see
  [Interoperability notes](#interoperability-notes)).
- Interactive password authentication works normally -- if the remote requires a password
  (or needs a host-key confirmation on first connect), Windows' OpenSSH client prompts for it
  right in the same terminal window, same as running `ssh` directly. SSH key-based auth is still
  recommended for convenience (and required for any unattended/scripted transfer), but typing a
  password interactively is fully supported.
- `-e "ssh -p 2222"` picks a non-default SSH port or extra ssh options, same as real rsync.

### Talking to another Windows machine over SSH

This needs the **remote** Windows machine to have:
1. `rsyncWindows` installed (so it has something to run as the "remote rsync"), and
2. OpenSSH **Server** enabled and running (Windows ships the OpenSSH client by default, but the
   *server* is a separate optional feature):
   ```powershell
   # on the REMOTE machine, elevated:
   Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
   Start-Service sshd
   Set-Service -Name sshd -StartupType Automatic
   ```

Then from the local machine, tell rsyncWindows to invoke `rsyncWindows` (not plain `rsync`, which
doesn't exist on the remote Windows box) on the far end:

```powershell
rsyncWindows -avz --rsync-path=rsyncWindows C:\Projects\myapp\ myuser@192.168.1.60:C:/Projects/myapp/
```

Note remote Windows paths after the colon use forward slashes (`C:/...`), matching how the
wire protocol carries paths.

### Talking to an rsync daemon (`rsync://`)

No SSH needed -- talks directly to a running daemon (ours or a real rsync's) over its configured
port:

```powershell
# push into a writable module
rsyncWindows -av C:\Backups\  rsync://192.168.1.50/backups/

# pull from a module
rsyncWindows -av backup-host::backups/2026/  C:\Restore\
```

See [Server usage](#server-usage-the-daemon--windows-service) below for setting up modules on
*this* machine's daemon.

### Supported options

| Option | Short | Effect |
|---|---|---|
| `--verbose` | `-v` | Print what's transferred |
| `--dry-run` | `-n` | Show what would transfer, change nothing |
| `--archive` | `-a` | Shorthand for `-rlptgoD` |
| `--recursive` | `-r` | Recurse into directories |
| `--links` | `-l` | Recreate symlinks at the destination (best-effort -- see note below) |
| `--perms` | `-p` | Apply best-effort permissions (owner-write bit -> Windows ReadOnly attribute) |
| `--times` | `-t` | Preserve modification times |
| `--owner` | `-o` | Include uid on the wire (no Windows-user mapping is applied on write) |
| `--group` | `-g` | Include gid on the wire (no Windows-group mapping is applied on write) |
| `--devices`, `--specials`, `-D` | | Recognized; device/special files are not created |
| `--compress` | `-z` | Compress file data in transit (real zlib) |
| `--compress-level=N` | | Zlib compression level |
| `--checksum` | `-c` | Parsed, not yet enforced (v1 always uses block checksums) |
| `--exclude=PATTERN`, `--include=PATTERN` | | Filter which files are sent (`*`, `**`, `?`, `/`-anchoring) |
| `--human-readable` | `-h` | Recognized |
| `--itemize-changes` | `-i` | Recognized |
| `--rsh=COMMAND` | `-e` | Remote shell to use (default `ssh`) |
| `--rsync-path=PATH` | | What to execute on the remote end (default `rsync`; use `rsyncWindows` for a Windows remote) |
| `--password-file=PATH` | | Daemon-mode (`rsync://`) auth: read the password from this file instead of prompting/env (never pass a real password on the command line) |
| `--stats` | | Recognized |
| `--dry-run` | `-n` | Preview only |

**Parsed but not yet acted on** (accepted so scripts using them don't fail argument parsing, but
have no effect in this version): `--exclude-from=FILE`/`--include-from=FILE` (the file is never
read -- use `--exclude`/`--include` directly for now), `--filter=RULE`/`-f`, `--delete` and its
`-before/-during/-delay/-after/-excluded` variants, `--update`, `--existing`, `--ignore-existing`,
`--relative`, `--files-from`, `--backup`/`--backup-dir`, `--bwlimit`,
`--partial`/`--partial-dir`/`-P`, `--progress`, `--out-format`, `--log-file`. If your workflow
depends on any of these actually taking effect, check back for a newer version before relying on
it.

**Symlinks (`-l`)**: recreated as Windows *file* symlinks. Creating any symlink on Windows needs
either Developer Mode turned on or an elevated process -- if neither is true, rsyncWindows skips
the symlink (prints a warning) rather than failing the whole transfer. A symlink whose real
target is a directory won't traverse correctly on Windows (file vs. directory symlinks are
different things on Windows, unlike Unix) -- a known limitation, not a silent one.

**Case collisions**: if a source tree contains two paths that differ only by case (valid on
Linux, e.g. `File.txt` and `file.txt` in the same folder), rsyncWindows refuses the transfer with
a clear error rather than silently letting one clobber the other on Windows' case-insensitive
filesystem.

---

## Server usage (the daemon / Windows Service)

The daemon speaks the real `rsync://` protocol (the same thing `rsyncd`/`rsync --daemon` speaks
on Linux) -- any real rsync client can connect to it, and it can connect to any real rsync
daemon.

### The config file

Location: **`%ProgramData%\rsyncWindows\rsyncd.conf`** (typically
`C:\ProgramData\rsyncWindows\rsyncd.conf`).

The service creates a starter file here automatically the first time it runs if one doesn't
exist yet. **Editing this file takes effect automatically within about a second** -- the service
watches it for changes and hot-reloads; you do not need to restart the service after editing it.

Format (rsyncd.conf-shaped -- one optional global setting, then any number of `[module]`
sections):

```ini
# Global (optional, before any [module] section):
port = 873

[backups]
    path = C:\Backups
    comment = Nightly backups
    read only = no
    list = yes
    hosts allow = 192.168.1.0/24
    hosts deny =

[secure-drop]
    path = C:\Uploads
    read only = no
    auth users = alice
    secrets file = C:\ProgramData\rsyncWindows\rsyncd.secrets
    list = no
```

A secrets file (referenced by `secrets file =` above) is a plain text file, one `user:password`
pair per line:

```
alice:hunter2
```

**Lock down the secrets file's permissions** (`icacls` or the Security tab) so only the service
account and administrators can read it -- rsyncWindows doesn't enforce a permissions check on it
itself.

### Module options reference

| Key | Default | Meaning |
|---|---|---|
| `path` | *(required)* | The local directory this module exposes |
| `comment` | *(empty)* | Shown in a module listing (`rsync host::`) |
| `read only` | `yes` | `no` to allow clients to push/upload into this module |
| `list` | `yes` | `no` to hide this module from a bare module listing |
| `hosts allow` | *(all allowed)* | Comma-separated IPs/CIDR ranges allowed to connect (e.g. `192.168.1.0/24`) |
| `hosts deny` | *(none)* | Same, denied. `hosts allow` takes precedence when both match. |
| `auth users` | *(none = no auth)* | Comma-separated usernames that must authenticate via the secrets file |
| `secrets file` | | Path to the `user:password` file for `auth users` |
| `max connections` | `0` (unlimited) | Recognized, not yet enforced |
| `timeout` | `0` (none) | Recognized, not yet enforced |

Not supported (Windows has no direct equivalent, or it's out of v1 scope): `uid`/`gid`, `chroot`,
per-module `exclude`/`include`/`filter`, `lock file`.

### Managing the service

Standard Windows Service, name **`rsyncWindows`**:

```powershell
Get-Service rsyncWindows
Start-Service rsyncWindows
Stop-Service rsyncWindows
Restart-Service rsyncWindows   # only needed if the exe itself was updated, NOT for config edits
```

Logs go to the Windows Event Log (Application log, source relates to the service's own .NET
logging) -- check there first if the service won't start (most often: the configured port is
already in use, or a `path =` in the config points somewhere the service account can't reach).

### The tray app

`RsyncWindows.Tray.exe` (installed to `...\rsyncWindows\tray\`) is a small system-tray icon with
a right-click menu. `install.ps1` registers it to start automatically at login (per-user, via the
`HKCU\...\Run` key) and launches it immediately after installing, so it should already be running
in your notification area -- if you don't see it, check the hidden-icons overflow arrow, or start
it manually from the path above.

- **Service: Running / Stopped** -- live status
- **Start Service** / **Stop Service**
- **Manage Shared Folders...** -- add/remove/edit the folders (`rsyncd.conf` modules) exposed to
  the network: module name, filesystem path, read-only, and which users are allowed. Prompts for
  administrator rights (UAC) -- exposing folders to the network is an admin-level decision, and
  the config file itself isn't writable by a non-admin.
- **Manage Allowed Users...** -- add/remove the username/password pairs remote clients authenticate
  with (backed by `rsyncd.secrets`). Also elevation-gated: the secrets file holds plaintext
  passwords (real rsync's own secrets-file format has no hashing) and is locked down on disk to
  Administrators/SYSTEM only after every save.
- **Edit Config (raw text)...** -- opens `rsyncd.conf` directly in your default text editor, for
  anything the two dialogs above don't cover (`hosts allow`/`hosts deny`, `max connections`, etc.)
- **Open Config Folder**
- **Start with Windows** -- checkable; toggles the per-user autostart entry the installer set up

Changes made through **Manage Shared Folders**/**Manage Allowed Users** take effect within about a
second -- the service polls `rsyncd.conf` for changes and hot-reloads, no restart needed. A folder
with no users listed in **Manage Shared Folders** has no login requirement (open to anyone who can
reach the port); give it at least one allowed user to require authentication.

It's optional and only for convenience -- the service runs independently of whether the tray app
is open.

### Firewall

The installer opens an inbound rule named **"rsyncWindows Daemon"** for the port you specify
(default 873/TCP). If you change the daemon's port in `rsyncd.conf`, either re-run
`install.ps1 -Port <newport>` or add/edit the firewall rule yourself.

---

## Interoperability notes

This has been tested directly against a real `rsync 3.5.0` binary (protocol version 32), in both
directions and over both transports:

- SSH-piped mode (`user@host:path`), our client <-> real rsync, both directions.
- Genuine daemon mode (`rsync://`), our daemon <-> real rsync client, both push and pull.
- Compression (`-z`), both directions, using real zlib under the hood on both ends.
- Delta/block-matching (re-syncing a modified file only sends the changed blocks), both
  directions, with and without compression.

Not yet covered by real-peer testing: ACLs, extended attributes, hardlink preservation, and
`--link-dest`-style incremental backups are permanently out of scope for v1 (Windows has no
clean equivalent for several of these).

---

## Troubleshooting

**"`rsyncWindows` is not recognized..."** -- open a *new* terminal window after installing (PATH
changes don't apply retroactively to already-open shells), or confirm the install actually added
it: `[Environment]::GetEnvironmentVariable("Path","Machine")` should contain the `cli` folder.

**Service won't start** -- check `Get-Service rsyncWindows | Format-List *` and the Application
event log. Common cause: the configured port is already bound by something else (another rsync
daemon, or a leftover process) -- change `port =` in `rsyncd.conf` or free the port.

**A push to a daemon module is refused** -- the module defaults to `read only = yes`. Set
`read only = no` in that module's section of `rsyncd.conf` (no restart needed).

**Symlinks aren't being created on the receiving end** -- check for a
`skipped symlink '...' : missing SeCreateSymbolicLinkPrivilege` message. Turn on Windows Developer
Mode (Settings -> Privacy & Security -> For Developers) or run the transfer elevated.

**"refusing to write: source path(s) differ only by case..."** -- the source tree has two paths
that are distinct on the sending Unix filesystem but would collide on Windows. Rename one of them
at the source, or exclude one with `--exclude`.

---

## Uninstalling

From an elevated PowerShell prompt, from this same `dist` folder:

```powershell
.\uninstall.ps1                  # keeps your rsyncd.conf
.\uninstall.ps1 -RemoveConfig     # also deletes rsyncd.conf and any secrets file
```
