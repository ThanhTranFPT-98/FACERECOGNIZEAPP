param(
    [string]$DotnetPath = 'dotnet',
    [string]$PythonPath = 'python',
    [switch]$Bootstrap
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$apiDirectory = Join-Path $projectRoot 'DSAVehicle'
$logDirectory = Join-Path $apiDirectory 'App_Data'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
if ($Bootstrap) {
    $password = Read-Host 'Initial admin password (at least 12 characters)' -AsSecureString
    $credential = New-Object System.Management.Automation.PSCredential('admin', $password)
    $env:Parking__BootstrapPassword = $credential.GetNetworkCredential().Password
    if ($env:Parking__BootstrapPassword.Length -lt 12) { throw 'Use at least 12 characters.' }
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
& $DotnetPath build (Join-Path $apiDirectory 'DSAVehicle.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed. Install the .NET 8 SDK.' }
$api = Start-Process -FilePath $DotnetPath -ArgumentList 'run --no-build --urls http://localhost:5085' -WorkingDirectory $apiDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDirectory 'parking-api.log') -RedirectStandardError (Join-Path $logDirectory 'parking-api-error.log')
$ocr = Start-Process -FilePath $PythonPath -ArgumentList '-m uvicorn plate_service:app --host 127.0.0.1 --port 8001' -WorkingDirectory (Join-Path $projectRoot 'AiService') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDirectory 'parking-ocr.log') -RedirectStandardError (Join-Path $logDirectory 'parking-ocr-error.log')
$ui = Start-Process -FilePath 'npm.cmd' -ArgumentList 'run dev' -WorkingDirectory (Join-Path $projectRoot 'ui') -WindowStyle Hidden -PassThru
Remove-Item Env:\Parking__BootstrapPassword -ErrorAction SilentlyContinue
Write-Host "Started API PID $($api.Id), OCR PID $($ocr.Id), UI PID $($ui.Id)."
Write-Host 'Open http://localhost:5173/parking. Check App_Data logs if a service fails to start.'
