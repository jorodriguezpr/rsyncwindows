<#
.SYNOPSIS
    Installs rsyncWindows: copies the published binaries, registers the daemon as a Windows
    Service, opens a firewall rule for it, and adds the client to the system PATH.

.DESCRIPTION
    Must be run from an elevated ("Run as Administrator") PowerShell prompt -- installing a
    Windows Service and editing the machine-wide PATH both require administrator rights.

    What this script does, in order:
      1. Copies dist\bin\cli, dist\bin\service, dist\bin\tray into -InstallDir
         (default: C:\Program Files\rsyncWindows).
      2. Registers RsyncWindows.Service.exe as a Windows Service named "rsyncWindows"
         (Automatic startup) and starts it. If a starter rsyncd.conf doesn't exist yet, the
         service creates one on first run at %ProgramData%\rsyncWindows\rsyncd.conf.
      3. Opens an inbound firewall rule for the daemon's port (873 by default) unless
         -NoFirewallRule is passed.
      4. Adds the cli\ folder to the machine-wide PATH (idempotent -- safe to re-run) unless
         -NoPathChange is passed, so `rsyncWindows` can be run from any new shell.

    Safe to re-run: an existing service is stopped/removed and re-registered, and PATH entries
    are de-duplicated rather than appended repeatedly.

.PARAMETER InstallDir
    Where to install the program files. Default: C:\Program Files\rsyncWindows

.PARAMETER Port
    The daemon port to open a firewall rule for. Only used for the firewall rule itself -- the
    actual listening port is whatever rsyncd.conf's own `port =` setting says (873 if unset).

.PARAMETER NoFirewallRule
    Skip creating the inbound firewall rule.

.PARAMETER NoPathChange
    Skip adding the client to the machine PATH.

.PARAMETER NoService
    Skip installing/starting the Windows Service (client-only install).

.EXAMPLE
    .\install.ps1
    Installs everything with defaults.

.EXAMPLE
    .\install.ps1 -InstallDir "D:\Tools\rsyncWindows" -Port 8730
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\rsyncWindows",
    [int]$Port = 873,
    [switch]$NoFirewallRule,
    [switch]$NoPathChange,
    [switch]$NoService
)

$ErrorActionPreference = "Stop"
$ServiceName = "rsyncWindows"
$FirewallRuleName = "rsyncWindows Daemon"

function Assert-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host "This script must be run as Administrator." -ForegroundColor Red
        Write-Host "Right-click PowerShell and choose 'Run as Administrator', then re-run this script." -ForegroundColor Yellow
        exit 1
    }
}

function Copy-Component([string]$Name, [string]$SourceSubdir) {
    $source = Join-Path $PSScriptRoot "bin\$SourceSubdir"
    if (-not (Test-Path $source)) {
        throw "Expected published binaries at '$source' but they weren't found. Run this script from inside the extracted 'dist' folder."
    }
    $target = Join-Path $InstallDir $SourceSubdir
    Write-Host "Installing $Name to $target ..."
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path "$source\*" -Destination $target -Recurse -Force
    return $target
}

function Install-DaemonService([string]$ServiceExeDir) {
    $exePath = Join-Path $ServiceExeDir "RsyncWindows.Service.exe"
    if (-not (Test-Path $exePath)) {
        throw "Service executable not found at '$exePath'."
    }

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Existing '$ServiceName' service found -- stopping and removing it first..."
        if ($existing.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $existing.WaitForStatus('Stopped', (New-TimeSpan -Seconds 15))
        }
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }

    Write-Host "Registering Windows Service '$ServiceName' ..."
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName "rsyncWindows Daemon" `
        -Description "rsync-compatible file sync daemon (rsyncWindows). Config: $env:ProgramData\rsyncWindows\rsyncd.conf" `
        -StartupType Automatic | Out-Null

    Write-Host "Starting '$ServiceName' ..."
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 1
    $status = (Get-Service -Name $ServiceName).Status
    Write-Host "Service status: $status" -ForegroundColor $(if ($status -eq 'Running') { 'Green' } else { 'Yellow' })
}

function New-FirewallRuleForDaemon([int]$RulePort) {
    Write-Host "Opening inbound firewall rule '$FirewallRuleName' for TCP port $RulePort ..."
    Remove-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $FirewallRuleName -Direction Inbound -Protocol TCP `
        -LocalPort $RulePort -Action Allow | Out-Null
}

function Add-ToMachinePath([string]$DirToAdd) {
    $current = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $parts = $current -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $DirToAdd.TrimEnd('\')) }
    $updated = ($parts + $DirToAdd) -join ';'
    [Environment]::SetEnvironmentVariable("Path", $updated, "Machine")
    Write-Host "Added '$DirToAdd' to the machine PATH."
    Write-Host "Open a NEW terminal window for the PATH change to take effect." -ForegroundColor Yellow
}

function Enable-TrayAutostart([string]$TrayExePath) {
    # HKCU, not HKLM: the tray is a per-user status icon (it has no desktop session to run in as
    # a service, unlike the daemon), so it belongs in the CURRENT user's own Run key, started at
    # THEIR next login -- not a machine-wide autostart that would (harmlessly but pointlessly)
    # try to launch a GUI app for every account, including ones that never log in interactively.
    # Since install.ps1 itself runs elevated via the SAME account (UAC keeps the same user, just
    # a higher-integrity token), HKCU here still resolves to the interactive user's own hive.
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "rsyncWindows Tray" -Value "`"$TrayExePath`""
    Write-Host "Registered the tray app to start automatically at login."
}

# ---------------------------------------------------------------------------

Assert-Admin

Write-Host ""
Write-Host "=== Installing rsyncWindows to $InstallDir ===" -ForegroundColor Cyan
Write-Host ""

$cliDir = Copy-Component "client (rsyncWindows.exe)" "cli"
$serviceDir = $null
if (-not $NoService) {
    $serviceDir = Copy-Component "daemon service" "service"
}
$trayDir = Copy-Component "tray status app" "tray"

if (-not $NoService) {
    Install-DaemonService $serviceDir
    if (-not $NoFirewallRule) {
        New-FirewallRuleForDaemon $Port
    }
}
else {
    Write-Host "Skipping service install (-NoService)." -ForegroundColor Yellow
}

if (-not $NoPathChange) {
    Add-ToMachinePath $cliDir
}
else {
    Write-Host "Skipping PATH change (-NoPathChange)." -ForegroundColor Yellow
}

$trayExe = Join-Path $trayDir "RsyncWindows.Tray.exe"
Enable-TrayAutostart $trayExe
Write-Host "Starting the tray app now ..."
try {
    Start-Process -FilePath $trayExe | Out-Null
}
catch {
    Write-Host "Couldn't launch the tray app automatically ($($_.Exception.Message)) -- it will still start at your next login, or run it manually from $trayExe" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host ""
Write-Host "Client:    $cliDir\rsyncWindows.exe"
if (-not $NoService) {
    Write-Host "Service:   '$ServiceName' (daemon), config at $env:ProgramData\rsyncWindows\rsyncd.conf"
}
Write-Host "Tray app:  $trayExe (icon in the notification area -- starts automatically at login)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Open a NEW terminal (for PATH to take effect) and run: rsyncWindows --version"
Write-Host "  2. Right-click the tray icon -> 'Manage Shared Folders...' to expose a folder, and"
Write-Host "     'Manage Allowed Users...' to set who can authenticate to it (both prompt for admin"
Write-Host "     rights via UAC, since they edit rsyncd.conf/rsyncd.secrets)."
Write-Host "  3. Restart the service after editing the config is NOT required -- it hot-reloads automatically."
Write-Host "  4. See docs\README.md in this dist folder for full client and server usage."
Write-Host ""
