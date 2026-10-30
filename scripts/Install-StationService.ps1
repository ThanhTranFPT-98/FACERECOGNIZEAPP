<#
.SYNOPSIS
  Installs (or removes) the station as the Windows service "DSAStation" (station plan §9).

.DESCRIPTION
  Starts by itself after Windows boots (delayed, so the network is up) and is restarted after a crash: after 5 s, 5 s, then 30 s,
  with the failure count reset after a day. Run from an elevated PowerShell. Nothing is changed with -WhatIf.

  1. Publish:   dotnet publish DSAVehicle -c Release -o C:\DSAStation\app
  2. Configure: C:\DSAStation\app\appsettings.Production.json (or machine environment variables): Parking__BootstrapPassword for the first start only,
                Receiver__Key, Parking__DeviceKey, Platform__*, Sync__MasterSource. Never commit them.
  3. Install:   .\scripts\Install-StationService.ps1 -PublishDir C:\DSAStation\app -Urls http://0.0.0.0:5085
  4. Remove:    .\scripts\Install-StationService.ps1 -Uninstall

  Windows 11 may restart for updates on its own; the service comes back after the reboot (check at staging, plan §10). Not yet tried on a station PC.

.EXAMPLE
  .\scripts\Install-StationService.ps1 -PublishDir C:\DSAStation\app -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PublishDir = 'C:\DSAStation\app',
    [string]$Urls = 'http://127.0.0.1:5085',
    [string]$Name = 'DSAStation',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

function Test-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
if (-not $WhatIfPreference -and -not (Test-Admin)) { throw 'Run this from an elevated PowerShell (Run as administrator), or use -WhatIf to see what it would do.' }

$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue

if ($Uninstall) {
    if (-not $existing) { Write-Host "Service $Name is not installed."; return }
    if ($PSCmdlet.ShouldProcess($Name, 'stop and delete the service')) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name $Name -Force }   # stops ffmpeg cleanly; killing the API leaves recorders running
        & sc.exe delete $Name | Out-Null
        Write-Host "Service $Name deleted. App_Data (database, evidence, video) was not touched."
    }
    return
}

$exe = Join-Path $PublishDir 'DSAVehicle.exe'
if (-not (Test-Path $exe)) {
    if ($WhatIfPreference) { Write-Warning "$exe does not exist yet; publish first (dotnet publish DSAVehicle -c Release -o $PublishDir)." }
    else { throw "$exe not found. Publish first: dotnet publish DSAVehicle -c Release -o $PublishDir" }
}
if ($existing) { throw "Service $Name already exists. Remove it first with -Uninstall." }

$binary = "`"$exe`" --urls $Urls"
if ($PSCmdlet.ShouldProcess($Name, "create the service: $binary")) {
    New-Service -Name $Name -DisplayName 'DSA Station (parking and access control)' -BinaryPathName $binary -StartupType Automatic `
        -Description 'DSA station software: receives Face-S, plate cameras and AI box results, decides and operates the barrier, syncs with the FPT Cloud.' | Out-Null
    & sc.exe config $Name start= delayed-auto | Out-Null
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    & sc.exe failureflag $Name 1 | Out-Null        # also restart when the process exits with an error code, not only when it crashes
    Start-Service -Name $Name
    Write-Host "Service $Name installed and started on $Urls. Logs: $(Join-Path $PublishDir 'App_Data')."
}
