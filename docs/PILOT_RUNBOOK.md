# Pilot and commissioning

## Discovery record

- Country: Vietnam (confirmed).
- Site, lanes, peak flow, controller/vendor, camera positions, plate dataset, retention period, operating policy: pending site input.
- Fees: excluded.

## Start in shadow mode

1. Keep every real lane in shadow mode. Configure camera ingestion and operator accounts.
2. Collect consented examples from cars and motorcycles: front/rear angles, two-line plates, day/night, rain/glare, blur, occlusion, helmets, unknowns and conflicting identities.
3. Label plate text, vehicle type and conditions. Run `python AiService/evaluate_plates.py <manifest.json>` from a Python environment containing OCR dependencies.
4. Record exact-match rate, latency, manual-review rate and false decisions per cohort. Define acceptance thresholds with the site operator; none have been measured yet.
5. Keep `Parking__ApprovedPlateModel` empty until a specific model/version passes evaluation. Empty approval forces OCR scores to zero for automatic authorization while retaining the candidate for review.
6. Evaluate face matching and spoof resistance separately. Existing face matching is not proof of liveness, especially with helmets/photos. Camera-based face authorization stays disabled until `Parking__EnableFaceAuthorization=true` is explicitly configured after that validation. Simulated face-policy tests do not require this setting.

## Hardware-in-the-loop

With a qualified installer, test: obstacle beam, presence loop, jam, power loss, emergency release, stale heartbeat, timeout after command acceptance, controller restart, duplicated command, tailgating, and exit without entry. Confirm the physical controller's close/interlock behavior. Follow `BARRIER_BRIDGE.md`.

## Supervised release

Enable one lane only after the above checks pass. A guard verifies decisions and can approve pending reviews. Review approvals expire in 30 seconds; unresolved observations older than five minutes need recapture. Blocked vehicles cannot be approved through ordinary review.

## Incident/rollback

Turn shadow mode on to stop new app-driven opening requests. The physical controller remains responsible for safe motion and emergency operation. Inspect unknown command outcomes before reconciliation; never resend a relay command just because the network timed out. Restore the prior application build only with a compatible database backup/migration plan.

## Backup and restore drill

SQLite: use `python scripts/backup_sqlite.py <parking.db> <new-backup.db>`. Protect evidence images and authentication keys in separate backups. For a consistent full data/evidence restore point, pause ingestion and retention during capture. Test restores to a separate database via `ConnectionStrings__Parking`; never overwrite a live database during a drill.

SQL Server: use the site's native database backup/restore procedure, restore to an isolated database, and run the operator workflow before switching connections. Do not switch providers against the same database.

## Still requires a human/site

Hardware selection and wiring, locally applicable biometric/data-retention decisions, model licensing review, measured accuracy targets, safety commissioning, and supervised pilot sign-off. Software tests cannot substitute for these activities.
