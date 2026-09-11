<#
.SYNOPSIS
    Uninstalls rsyncWindows: stops and removes the Windows Service, closes its firewall rule,
    removes the client from the machine PATH, and (optionally) deletes the installed files.

.DESCRIPTION
    Must be run from an elevated ("Run as Administrator") PowerShell prompt.

    By default this KEEPS %ProgramData%\rsyncWindows\rsyncd.conf (your module configuration) --
    pass -RemoveConfig to also delete it.

.PARAMETER InstallDir
    Where the program files were installed. Default: C:\Program Files\rsyncWindows

.PARAMETER RemoveConfig
    Also delete %ProgramData%\rsyncWindows (the rsyncd.conf module configuration).

.PARAMETER KeepFiles
    Don't delete -InstallDir itself -- only unregister the service/firewall rule/PATH entry.

.EXAMPLE
    .\uninstall.ps1

.EXAMPLE
    .\uninstall.ps1 -RemoveConfig
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\rsyncWindows",
    [switch]$RemoveConfig,
    [switch]$KeepFiles
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

Assert-Admin

Write-Host ""
Write-Host "=== Uninstalling rsyncWindows ===" -ForegroundColor Cyan
Write-Host ""

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Stopping and removing service '$ServiceName' ..."
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds 15))
    }
    & sc.exe delete $ServiceName | Out-Null
}
else {
    Write-Host "Service '$ServiceName' not installed -- skipping."
}

if (Get-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue) {
    Write-Host "Removing firewall rule '$FirewallRuleName' ..."
    Remove-NetFirewallRule -DisplayName $FirewallRuleName
}
else {
    Write-Host "Firewall rule '$FirewallRuleName' not found -- skipping."
}

Write-Host "Stopping the tray app (if running) and removing its startup entry ..."
Get-Process -Name "RsyncWindows.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "rsyncWindows Tray" -ErrorAction SilentlyContinue

$cliDir = Join-Path $InstallDir "cli"
$current = [Environment]::GetEnvironmentVariable("Path", "Machine")
if ($current -and ($current -split ';' | Where-Object { $_.TrimEnd('\') -eq $cliDir.TrimEnd('\') })) {
    Write-Host "Removing '$cliDir' from the machine PATH ..."
    $updated = ($current -split ';' | Where-Object { $_.TrimEnd('\') -ne $cliDir.TrimEnd('\') }) -join ';'
    [Environment]::SetEnvironmentVariable("Path", $updated, "Machine")
}
else {
    Write-Host "'$cliDir' not found on the machine PATH -- skipping."
}

if (-not $KeepFiles -and (Test-Path $InstallDir)) {
    Write-Host "Removing installed files at $InstallDir ..."
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

$configDir = Join-Path $env:ProgramData "rsyncWindows"
if ($RemoveConfig -and (Test-Path $configDir)) {
    Write-Host "Removing config at $configDir ..." -ForegroundColor Yellow
    Remove-Item -Path $configDir -Recurse -Force -ErrorAction SilentlyContinue
}
elseif (Test-Path $configDir) {
    Write-Host "Keeping config at $configDir (pass -RemoveConfig to delete it too)."
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host ""
