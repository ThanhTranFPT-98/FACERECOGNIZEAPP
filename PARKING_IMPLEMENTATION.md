# Parking implementation status — 2026-09-08

The application now has a persistent parking workflow at `/parking`. This replaces the earlier in-memory screen. The old development-only simulation endpoint and its tests remain as an isolated example.

## Status against the plan

| Stage | Software delivered | Validation / remaining site work |
| --- | --- | --- |
| 0 — Discovery | Vietnam plate handling; fees excluded; configurable lanes and policies | Camera/controller models, site layout, traffic targets, consent/retention requirements still need site input. |
| 1 — Foundation | Cookie login, CSRF, Admin/Guard/Auditor roles, password hashing, account deactivation, persistent SQLite/SQL Server contexts and migrations, protected evidence access, encrypted new face templates, audit and health | SQLite and auth tested. SQL Server schema script generated; server execution and production key/HTTPS configuration remain site work. Legacy templates are protected when rewritten, not bulk-migrated. |
| 2 — Recognition | Local OpenCV/Tesseract OCR baseline, one/two-line civilian Vietnam formats, optional existing face-service integration, camera worker with burst consensus, model-version approval gate and evaluation runner | Tesseract engine and face weights are external prerequisites. No camera dataset or measured accuracy supplied. Baseline OCR requires review until a model version is approved. Special plates, lane tracking and liveness need site-specific validation/development. |
| 3 — Access and visitors | Five policies, vehicle activation/blocking, primary/shared-driver linking, time-limited visitor passes, entry quotas, review/deny/approve with reason, expiring decisions | Tested against actual API. Shared vehicles can link multiple enrolled people. Visitor management uses operator accounts; a separate resident self-service portal is not included. |
| 4 — Barrier | Persistent, idempotent commands; opt-in automatic opening; simulation; HTTP bridge adapter; fresh sensor gates; unknown-outcome handling; reconciliation; confirmed entry/exit sessions | Tested against local fake OCR/controller including malformed acknowledgments. Vendor bridge implementation, physical wiring, safety interlocks and commissioning require the actual hardware. Physical closing belongs to its safety controller. |
| 5 — Operations | Operator UI, SignalR updates, visitor/vehicle screens, search and audit, device status, alerts/acknowledgment, HTTPS webhook retry, evidence-retention worker, backup utility | UI build passes; no connected browser was available for visual checks. External alert delivery awaits a configured destination. |
| 6 — Pilot | Shadow mode, evaluation script, backup/restore guidance, commissioning and rollback runbooks | No real-site shadow run, hardware safety test, performance target validation or supervised pilot has occurred. |

## Run and review

See [deployment instructions](docs/DEPLOYMENT.md), [barrier contract](docs/BARRIER_BRIDGE.md), and [pilot runbook](docs/PILOT_RUNBOOK.md).

The initial admin password is supplied by the operator; no default credential exists. The new launcher prompts for it with `scripts/Start-Parking.ps1 -Bootstrap`. Startups after initialization do not need the bootstrap setting.

Both default lanes are shadow/simulated. Database records survive restart. For a full simulated flow: register a plate, configure a simulated lane with shadow mode off, evaluate an observation, request open, then simulate passage. Repeat on an exit lane to close the parking session.

## Tests

From the repository directory:

```powershell
dotnet run --project tests/ParkingCore.Tests
dotnet run --project tests/ParkingSimulation.Tests
python tests/integration_parking.py
python -m unittest discover -s AiService -p test_plate_service.py -v
python -m unittest discover -s tests -p test_backup.py -v
```

The Python integration test expects the API already built in Debug. Set `PARKING_DOTNET` when the SDK executable is not on PATH. It creates an isolated database, random credentials and local fake devices, verifies the workflow, restarts the API to verify persistence, and removes its temporary database. It never contacts real hardware or an external notification endpoint.

Verified: 15 core/crypto checks, 14 original simulation checks, 4 OCR validation/failure tests, a live-WAL backup/restore test, and an HTTP integration scenario covering authentication/CSRF/roles, policies, visitor quota and exit, guard review, device authentication, sensor gating, fake controller acknowledgment, command idempotency, shared drivers, burst consensus, automatic opening and persistence.

## Design limits

- One API process per database/site; command serialization is process-local, not distributed.
- New parking data uses a separate database from the existing face subsystem. Provider switching is a migration project, not simply pointing another provider at the same file/database.
- API audit is append-only through application endpoints; it is not tamper-proof against the database administrator.
- Entry quotas are conservatively reserved when an opening is requested. A failed/uncertain physical result does not automatically refund an entry.
- Camera evidence cannot authorize by an unapproved OCR model or disabled face authorization. Simulation input never operates a live lane.
- No photo/video spoof detection, hardware certification, real-world accuracy claim, parking billing, or external deployment is implied.

The software implementation is reviewable and runnable locally; the full production plan is not complete until its external prerequisites and site acceptance stages are completed.

## Final verification

- Backend build and all listed automated checks passed.
- Frontend production build passed. A large-bundle warning remains.
- npm and NuGet audits report no known vulnerabilities in the resolved JavaScript/.NET dependency graphs after updates.
- Python source compilation passed. Tesseract itself is not installed here, so OCR was tested for contract/validation and with a fake AI provider, not measured on real plates.
- Browser verification was unavailable because no browser was connected.
- SQL Server migration SQL was generated; physical SQL Server execution, real cameras, controller and pilot validation remain outstanding.
