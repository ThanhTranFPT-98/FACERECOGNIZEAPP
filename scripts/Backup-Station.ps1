<#
.SYNOPSIS
  Backs up the station's SQLite database (station plan §9) and keeps the newest N copies.

.DESCRIPTION
  Uses scripts/backup_sqlite.py (SQLite backup API: consistent while the station runs, integrity-checked, never overwrites).
  Evidence images and video are NOT in this backup: the evidence folder needs its own protected copy (see docs/DEPLOYMENT.md).
  -Register adds a daily Windows scheduled task that runs this script; nothing is changed with -WhatIf.

.EXAMPLE
  .\scripts\Backup-Station.ps1 -Database C:\DSAStation\app\App_Data\parking.db -Destination D:\DSABackups -Keep 14
  .\scripts\Backup-Station.ps1 -Database C:\DSAStation\app\App_Data\parking.db -Destination D:\DSABackups -Register -At 02:00
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Destination,
    [int]$Keep = 14,
    [string]$Python = 'python',
    [switch]$Register,
    [string]$At = '02:00',
    [string]$TaskName = 'DSAStationBackup'
)
$ErrorActionPreference = 'Stop'
if ($Keep -lt 1) { throw '-Keep must be at least 1.' }
$script = Join-Path $PSScriptRoot 'backup_sqlite.py'

if ($Register) {
    $self = $MyInvocation.MyCommand.Path
    $argument = "-NoProfile -ExecutionPolicy Bypass -File `"$self`" -Database `"$Database`" -Destination `"$Destination`" -Keep $Keep -Python `"$Python`""
    if ($PSCmdlet.ShouldProcess($TaskName, "register a daily task at ${At}: powershell $argument")) {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument
        $trigger = New-ScheduledTaskTrigger -Daily -At $At
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        Write-Host "Task $TaskName registered: every day at $At."
    }
    return
}

if (-not (Test-Path $Database)) { throw "Database not found: $Database" }
$target = Join-Path $Destination ("parking-{0}.db" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
if ($PSCmdlet.ShouldProcess($target, 'back up the database')) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & $Python $script $Database $target
    if ($LASTEXITCODE -ne 0) { throw "Backup failed (exit code $LASTEXITCODE); older copies were kept." }
    # Keep the newest N only after the new copy is verified.
    Get-ChildItem -Path $Destination -Filter 'parking-*.db' | Sort-Object Name -Descending | Select-Object -Skip $Keep | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        Write-Host "Removed old backup $($_.Name)"
    }
    Write-Host "Backup written: $target"
}
