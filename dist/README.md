# rsyncWindows -- distribution folder

This folder contains everything needed to install and run rsyncWindows: a self-contained
(no separate .NET install required) build of the client, the daemon service, and the tray app.

## Quick start

From an **elevated** PowerShell prompt, in this folder:

```powershell
.\install.ps1
```

Then open a **new** terminal and run:

```powershell
rsyncWindows --version
```

## Layout

```
dist/
  install.ps1       <- run this to install (see below)
  uninstall.ps1      <- run this to remove everything
  docs/
    README.md        <- full usage guide: client CLI options, server/daemon config, examples
  bin/
    cli/             <- rsyncWindows.exe (the client) + runtime
    service/         <- RsyncWindows.Service.exe (the daemon, installed as a Windows Service)
    tray/            <- RsyncWindows.Tray.exe (system-tray status/admin app)
```

**Full documentation, including every client option, the daemon config file format, and worked
examples (Windows<->Linux, Windows<->Windows, daemon mode) is in
[`docs/README.md`](docs/README.md).**

## License

Copyright 2026 Jose Rodriguez Arroyo.
Licensed under the Apache License, Version 2.0 -- see [`LICENSE`](../LICENSE) at the repository
root, or <http://www.apache.org/licenses/LICENSE-2.0>.
