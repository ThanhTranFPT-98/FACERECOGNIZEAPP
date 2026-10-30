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

Manual backend startup from `DSAVehicle`:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# Set Parking__BootstrapPassword securely for the first startup only.
dotnet run --urls http://localhost:5085
```

Manual OCR startup from `AiService`: `python -m uvicorn plate_service:app --host 127.0.0.1 --port 8001`.
Manual UI startup from `ui`: `npm run dev`.

The app creates a persistent local SQLite parking database at `DSAVehicle/App_Data/parking.db` and seeds two shadow/simulated lanes. It does not overwrite or migrate the original face SQL Server database. Parking records and original employee/face records are separate bounded areas.

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

## 24/7 video and cloud upload (CR-001)

Both features are **off by default**. They run inside the API process on the site station.

1. Install ffmpeg on the station PC and set `Video__FfmpegPath` if it is not on PATH.
2. Choose a video directory on a large disk (`Video__Directory`) and a quota below the disk size (`Video__LocalQuotaGb`). At ~2 Mbps, one camera needs ~21 GB per day.
3. Set `Video__Enabled=true`, restart the API, then add cameras in **Parking → Cameras & video** (admin). Link each camera to its lane so events show their video. The RTSP URL, including its password, is stored encrypted; back up the Data Protection key ring with the database.
4. Cloud: create a bucket at an S3-compatible provider with a data centre in Vietnam and add a **lifecycle rule that deletes objects after 30 days** (the application never deletes cloud objects). Then set `Cloud__Enabled=true`, `Cloud__Provider=S3`, `Cloud__Endpoint`, `Cloud__Bucket`, `Cloud__Region` (if the provider needs one), `Cloud__SiteId` (unique per site) and the keys through a secret store, never in committed files.
5. Optional: `Cloud__UploadMbpsCap` limits upload bandwidth; `Video__NtpServer` enables an hourly clock check (`CLOCK_DRIFT` alert above `Video__MaxClockDriftSeconds`).

Behaviour:

- Recording: one ffmpeg per camera, stream copy (no re-encoding), `.ts` segments named by the station clock. A camera without new segments for 3 × `SegmentSeconds` raises `CAMERA_NOT_RECORDING` and is restarted.
- Upload order: decision/command/session/alert records and their evidence photos first, then video, oldest first. Failures retry with backoff (up to 30 min). `UPLOAD_BACKLOG` fires when data older than `Cloud__BacklogAlertMinutes` is still waiting.
- Local disk: over quota, the oldest **uploaded** segments are deleted first. If it stays over quota, `VIDEO_DISK_FULL` (Critical) fires; below `Video__MinFreeDiskFraction` free space the oldest un-uploaded segments are discarded and audited, so the disk and database never fill completely. Without cloud upload the station behaves as a plain local recorder.
- Playback: the event **Video** button plays the segments ±10 s around the decision. Local files are remuxed to MP4 by ffmpeg; files already removed locally open through a 10-minute signed cloud link.
- If the API process is killed abruptly, its ffmpeg processes keep recording until the API starts again and stops them (`ffmpeg.pid`). Stop the API normally (service stop / Ctrl+C) where possible.

Test locally without a camera or cloud account: in Development, a camera source `file:C:\path\sample.ts` loops a file in real time, and `Cloud__Provider=Folder` with `Cloud__FolderPath` copies uploads to a folder. `tests/integration_video.py` does this automatically when `PARKING_FFMPEG` is set.

## Production preparation

Build the UI with `npm run build` and the API with `dotnet publish -c Release`. Serve `ui/dist` and proxy `/api`, `/parkingHub`, and the administrator-only legacy `/notificationHub` under one HTTPS origin. Configure trusted proxy forwarding and WebSocket support for the actual hosting environment. Production cookies require HTTPS. Keep Swagger/development mode off externally.

Use one API process per site/database. Database migrations run during startup; take backups before upgrades. SQL Server migrations were generated and their SQL script validated, but no SQL Server instance was available for an execution test. SQLite migrations and persistence were exercised in integration tests.

As a Windows service (CR-021): `dotnet publish DSAVehicle -c Release -o C:\DSAStationpp`, put the secrets in `appsettings.Production.json` there, then from an elevated PowerShell `scripts\Install-StationService.ps1 -PublishDir C:\DSAStationpp -Urls http://0.0.0.0:5085` (add `-WhatIf` to see what it does; `-Uninstall` removes it). The service starts after boot, restarts after a crash and stops ffmpeg cleanly when stopped. Daily database backup: `scripts\Backup-Station.ps1 -Database C:\DSAStationpp\App_Data\parking.db -Destination D:\DSABackups -Keep 14 -Register`. Evidence images and video need their own backup. These scripts have not been run on a real station PC yet.

Neither the UI build nor API tests constitute a physical installation test. Follow `PILOT_RUNBOOK.md` and `BARRIER_BRIDGE.md` before activating a real lane.

## Reference documentation

- [Tesseract installation](https://tesseract-ocr.github.io/tessdoc/Installation.html)
- [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie)
- [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery)
- [Vite 8 runtime requirements](https://vite.dev/blog/announcing-vite8)
