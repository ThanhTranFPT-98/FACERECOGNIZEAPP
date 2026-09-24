# Running the parking application

## Requirements

- .NET 8 SDK for build and development; ASP.NET Core runtime for published API.
- Node 20.19+ or 22.12+ (tested here with Node 24); the Vite upgrade requires this newer runtime.
- Python 3.12 and `AiService/requirements-plate.txt` for plate OCR.
- Tesseract executable and English OCR data. Vietnamese plate characters are Latin letters and digits; this baseline uses `eng`, not Vietnamese-language sentence recognition.
- Optional existing face stack: SQL Server, face model weights, and the dependencies in `AiService/requirements.txt`.

## First local startup

From `ui`, run `npm ci`. From a Python virtual environment, run `python -m pip install -r AiService/requirements-plate.txt` at the repository root. Install the Tesseract engine using its official installation guidance, then set `TESSERACT_CMD` to the executable path if it is not on PATH.

Run from the repository root:

```powershell
.\scripts\Start-Parking.ps1 -Bootstrap
```

The script prompts for the initial admin password and starts services in hidden windows. Open `http://localhost:5173/parking` and sign in as `admin`. Subsequent starts omit `-Bootstrap`. There is no built-in password. Existing launcher scripts predate authenticated parking startup; use the new script or the manual commands below.

During this development session, a temporary SDK was installed under `%TEMP%\parking-dotnet-sdk\sdk`. If it is still present, the launcher accepts `-DotnetPath "$env:TEMP\parking-dotnet-sdk\sdk\dotnet.exe"`. Install a normal SDK for ongoing work; the temporary directory is not a permanent dependency.

Manual backend startup from `MyfirstASP_NET`:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# Set Parking__BootstrapPassword securely for the first startup only.
dotnet run --urls http://localhost:5085
```

Manual OCR startup from `AiService`: `python -m uvicorn plate_service:app --host 127.0.0.1 --port 8001`.
Manual UI startup from `ui`: `npm run dev`.

The app creates a persistent local SQLite parking database at `MyfirstASP_NET/App_Data/parking.db` and seeds two shadow/simulated lanes. It does not overwrite or migrate the original face SQL Server database. Parking records and original employee/face records are separate bounded areas.

## Demo flow

1. Register `59-X1 123.45` as a motorcycle and an owner in Vehicles.
2. In Lane monitor, simulate `59X112345` at confidence `0.99` on `entry-1`. Expect Allow.
3. Request open while shadow mode is on. Expect a refusal explaining shadow mode.
4. In Lane settings, load `entry-1`, turn shadow mode off, keep simulated hardware on, and save.
5. Capture a new simulated observation and request opening within 30 seconds.
6. Simulate passage on the command. A parking session now represents the confirmed entry.
7. Repeat for exit using `exit-1`; confirmed exit closes the session. A used visitor pass can exit against its confirmed entry session.
8. Try unknown plates and low scores, then approve or deny with a guard reason. Review expires after five minutes; approval grants only 30 seconds to act.

## Configuration

Use environment variables, a secret manager, or .NET user secrets. `appsettings.Parking.example.json` is a reference, not automatically loaded.

| Setting | Meaning |
| --- | --- |
| `Parking__BootstrapUsername`, `Parking__BootstrapPassword` | Initial administrator, only consumed if the user table is empty. |
| `Parking__Provider=SqlServer` | Select SQL Server parking context and its separate migrations. Default is SQLite. |
| `ConnectionStrings__Parking` | Dedicated parking database connection. Do not point at the original face database: table names can overlap. |
| `ConnectionStrings__DefaultConnection` | Original face/employee SQL Server database. |
| `Parking__PlateAiUrl` | Trusted internal plate service, default loopback port 8001. |
| `Parking__FaceAiUrl` | Existing trusted face service, default loopback port 8000. |
| `Parking__ApprovedPlateModel` | Exact evaluated model version. Empty by default: OCR candidates require review. |
| `Parking__EnableFaceAuthorization` | Default false; enable only after face matching/spoof testing. |
| `Parking__DeviceKey` | Random 32+ character device credential. Set `PARKING_DEVICE_KEY` to the same value for Python workers. |
| `Parking__EnableLiveBarrier` | Default false. Additional lane and sensor gates must pass. |
| `Parking__BarrierUrl`, `Parking__BarrierKey` | Trusted controller bridge endpoint and credential. |
| `Parking__AlertWebhookUrl`, `Parking__AlertWebhookKey` | Optional HTTPS notification integration. No outbound notifications until configured. |
| `Parking__EvidenceRetentionDays` | Positive value enables deletion of expired snapshots. Zero disables automatic deletion until site policy is chosen. |
| `Parking__EvidenceDirectory` | Optional protected evidence directory; default `App_Data/evidence`. |
| `TESSERACT_CMD` | OCR executable path. |

Do not commit credentials, images, or generated databases. Configuration URLs are operator-trusted network endpoints; never let public users set them.

## Cameras and face enrollment

Run `python AiService/lane_worker.py --source <camera-index-or-RTSP-URL> --lane entry-1` with `PARKING_DEVICE_KEY` configured. It sends bounded, retried frame bursts with correlation IDs. At least two usable frames must agree, no usable frame may conflict, and total burst processing is limited to 20 seconds. It does not invent motor safety feedback or send relay commands. Guard operators can request opening from accepted decisions. Per-lane automatic opening is off by default; when enabled, accepted camera observations use the same command and safety gates. Camera/loop-trigger tuning and lane tracking require the actual installation.

Existing face enrollment screens are administrator-only. Link primary and additional enrolled person IDs to a vehicle for face policies. The Python face service now reads embeddings through the device-authenticated `/api/v1/parking/device/face-templates` endpoint. Set `PARKING_API_URL` if its backend is not on loopback port 5085. Keep AI services on loopback or an isolated internal network.

New/updated face template vectors are protected with ASP.NET Data Protection. Legacy plaintext vectors remain readable and are protected when rewritten; no bulk rewrite of the user's database was performed. Back up and secure the Data Protection key ring with the database: losing those keys makes encrypted templates unreadable. Key storage and at-rest protection must be configured for the production host/account. Existing model weights were not supplied or evaluated in this session.

## Production preparation

Build the UI with `npm run build` and the API with `dotnet publish -c Release`. Serve `ui/dist` and proxy `/api`, `/parkingHub`, and the administrator-only legacy `/notificationHub` under one HTTPS origin. Configure trusted proxy forwarding and WebSocket support for the actual hosting environment. Production cookies require HTTPS. Keep Swagger/development mode off externally.

Use one API process per site/database. Database migrations run during startup; take backups before upgrades. SQL Server migrations were generated and their SQL script validated, but no SQL Server instance was available for an execution test. SQLite migrations and persistence were exercised in integration tests.

Neither the UI build nor API tests constitute a physical installation test. Follow `PILOT_RUNBOOK.md` and `BARRIER_BRIDGE.md` before activating a real lane.

## Reference documentation

- [Tesseract installation](https://tesseract-ocr.github.io/tessdoc/Installation.html)
- [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie)
- [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery)
- [Vite 8 runtime requirements](https://vite.dev/blog/announcing-vite8)
