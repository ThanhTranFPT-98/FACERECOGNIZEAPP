# Chay phan mem tram (DSA_Vehicles) tren LAPTOP, noi vao Cloud chay tren may ban qua Tailscale Serve (HTTPS).
# Dat file nay vao thu muc da clone (co thu muc DSAVehicle), roi chay:
#   powershell -ExecutionPolicy Bypass -File .\tram-laptop.ps1
param(
    [string]$CloudUrl = "https://desktop-1ed4js1.tailfc987d.ts.net",
    [string]$StationId = "CUALO-GC",
    [string]$Repo = $(if (Test-Path (Join-Path $PSScriptRoot "DSAVehicle")) { $PSScriptRoot } else { Join-Path $HOME "FACERECOGNIZEAPP" })
)
$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 may still default to TLS 1.0/1.1, which the HTTPS endpoint refuses.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
function Ok($t) { Write-Host "  OK  $t" -ForegroundColor Green }
function Bad($t) { Write-Host "  LOI $t" -ForegroundColor Red; exit 1 }

Write-Host "=== 1. Kiem tra cong cu" -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $Repo "DSAVehicle\DSAVehicle.csproj"))) { Bad "Khong thay code tram o $Repo. Clone: git clone https://github.com/ThanhTranFPT-98/FACERECOGNIZEAPP.git" }
Ok "code tram: $Repo"
# SDK 9/10 build net8.0 fine. What the station really needs is the .NET 8 runtime.
$sdks = @(& dotnet --list-sdks 2>$null)
$sdkOk = @($sdks -match '^(8|9|[1-9][0-9])\.')
$run8 = @(@(& dotnet --list-runtimes 2>$null) -match '^Microsoft\.AspNetCore\.App 8\.')
if (-not $sdkOk) { Bad ".NET SDK 8 tro len chua cai. Tai: https://dotnet.microsoft.com/download/dotnet/8.0" }
if (-not $run8) { Bad ".NET 8 runtime (ASP.NET Core 8) chua cai. Tai: https://dotnet.microsoft.com/download/dotnet/8.0" }
Ok ".NET SDK: $($sdkOk[-1])"
$branch = (& git -C $Repo rev-parse --abbrev-ref HEAD 2>$null)
if ($branch -ne "main") { Write-Host "  ! nhanh dang la '$branch', nen la main (git checkout main)" -ForegroundColor Yellow } else { Ok "nhanh: $branch" }

Write-Host "=== 2. Kiem tra duong toi Cloud ($CloudUrl)" -ForegroundColor Cyan
try { $h = Invoke-RestMethod "$CloudUrl/health" -TimeoutSec 8; Ok "Cloud tra loi: status = $($h.status)" }
catch { Bad "Khong toi duoc Cloud. Kiem tra: Tailscale tren laptop da bat va cung tai khoan? Tren may Cloud: Cloud dang chay (cong 5000) va da chay 'tailscale serve --bg 5000'?" }

Write-Host "=== 3. Khoa tram" -ForegroundColor Cyan
Write-Host "  Tren Dashboard Cloud (may ban): Tram -> $StationId -> Doi khoa -> copy, roi dan vao day."
$key = (Read-Host "  Khoa tram").Trim()
try { [void](Invoke-RestMethod "$CloudUrl/api/v1/stations/$StationId/master" -Headers @{ "X-Station-Key" = $key } -TimeoutSec 8); Ok "Cloud chap nhan khoa nay cho tram $StationId" }
catch { Bad "Cloud tu choi khoa ($([int]$_.Exception.Response.StatusCode)). Copy lai khoa moi nhat tren Dashboard." }

# Admin password of the station UI on this laptop: created once, kept next to the station database.
$data = Join-Path $env:LOCALAPPDATA "dsa-tram-thu"
New-Item -ItemType Directory -Force $data | Out-Null
$pwFile = Join-Path $data "admin-password.txt"
if (-not (Test-Path $pwFile)) { $b = New-Object byte[] 12; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); Set-Content $pwFile ([Convert]::ToBase64String($b)) -Encoding ASCII }
$stationPw = (Get-Content $pwFile -Raw).Trim()

$env:ASPNETCORE_ENVIRONMENT     = "Development"            # Production: the station UI cookie only travels over HTTPS
$env:Parking__Provider          = "Sqlite"
$env:ConnectionStrings__Parking = "Data Source=" + (Join-Path $data "parking.db")
$env:Parking__BootstrapPassword = $stationPw
$env:Parking__AlertWebhookUrl   = ""
$env:Parking__StationId         = $StationId               # S1: must equal Platform:StationId
$env:Platform__Enabled          = "true"
$env:Platform__Url              = $CloudUrl
$env:Platform__StationId        = $StationId
$env:Platform__Key              = $key
$env:Sync__MasterSource         = "Cloud"
# Do not print every SQL query. The name has dots, so $env:Name = ... would read them as property access.
[Environment]::SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command", "Warning", "Process")

Write-Host "=== 4. Chay tram (cong 5085)" -ForegroundColor Cyan
Write-Host "  Giao dien tram: mo cua so khac, chay:  cd `"$Repo\ui`"; npm install; npm run dev   -> http://localhost:5173"
Write-Host "  Dang nhap tram: admin / $stationPw" -ForegroundColor Yellow
Write-Host "  Du lieu tram: $data (xoa thu muc nay de bat dau lai)"
dotnet run --project (Join-Path $Repo "DSAVehicle") --urls http://localhost:5085

