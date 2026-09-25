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

Enable one lane only after the above checks pass. A guard verifies decisions and can approve pending reviews. Review approvals expire after the lane's decision validity (default 30 s); unresolved observations older than the review window (default 5 min) need recapture. Both are set in *Lane settings*. Blocked vehicles cannot be approved through ordinary review.

## Incident/rollback

Turn shadow mode on to stop new app-driven opening requests. The physical controller remains responsible for safe motion and emergency operation. Inspect unknown command outcomes before reconciliation; never resend a relay command just because the network timed out. Restore the prior application build only with a compatible database backup/migration plan.

## Backup and restore drill

SQLite: use `python scripts/backup_sqlite.py <parking.db> <new-backup.db>`. Protect evidence images and authentication keys in separate backups. For a consistent full data/evidence restore point, pause ingestion and retention during capture. Test restores to a separate database via `ConnectionStrings__Parking`; never overwrite a live database during a drill.

SQL Server: use the site's native database backup/restore procedure, restore to an isolated database, and run the operator workflow before switching connections. Do not switch providers against the same database.

## Plan V2 commissioning (CR-002 … CR-013)

Do these per station, in this order. Each step has a check you can see in the UI. Keep every lane in shadow mode until step 9.

1. **Station identity and site profile.** Set `Parking__StationId` (e.g. `GATE-II`) and confirm the time zone in the DB default (`Asia/Ho_Chi_Minh`). In *Lane settings*, set the site timing defaults and plate formats (CR-003, CR-006). Check: *Lane settings* shows the values.
2. **Areas.** Create the areas with the **same codes on every station** (e.g. `MINE`, `GATE2`, `MOTO`). Codes, not names, link presence records between stations. Then add locations, move each lane into its location, and add shifts per area plus overstay minutes (CR-002, CR-010). Check: *Areas* lists every lane.
3. **Accounts.** Create guard, safety officer, manager and technician accounts with expiry dates, and limit guards to their areas (CR-004/005). Check: sign in as a guard and see only that gate.
4. **AI cameras.** Set `Receiver__Key` (≥ 32 chars). Cameras that can only post to `host:port/api` also need `Receiver__Port`, `Receiver__AllowedIps` (camera subnet only) and the port in `--urls`. Open that port in the Windows firewall **only toward the camera network**. In *Cameras & video*, enter each device's MAC and channel. Leave *Trusted confidence* empty (CR-007). Check: *Device health → AI camera inbox* counts messages; no `UNKNOWN_CAMERA` alert.
5. **Field evaluation of camera results.** Run in shadow mode for an agreed period. Compare camera plates with what guards saw (use *Reports* → Excel, sheet Events). Only after the operator signs off, set *Trusted confidence* per camera. Without it, every camera result goes to guard review.
6. **Face terminals and PPE** (mine shaft). Requirement (Q7, 2026-09-25): the flap opens only after face + PPE are combined. Set the ZKTeco terminal to *verify only* and wire the flap to our I/O controller (option A), or wire ZKTeco's relay in series with our "PPE OK" relay (option B); see CR-008. Turn on *Auto-open* for the flap lane; its controller heartbeat reports person present / flap closed. Test: a worker with a face match but no helmet must NOT get through. Map the terminal and the PPE camera to the flap lane. Set *Lane controls* = People (`Subject = Person`), policy `FaceOnly`, and PPE rules starting with `warn` for every item (O5). Enter the workers in *People*. PPE messages must carry a frame counter or timestamp. Check: a test walk-through shows the PPE column on the event.
7. **Stations talking to each other.** Give all stations the same `Sync__Key`. Set `Sync__Peers` to the other stations' LAN URLs and `Sync__CloudPeers` to their cloud site IDs (option E). Check: enter at one gate, see the person in *Inside now* at another gate within seconds. Unplug the LAN cable, repeat, and see it arrive via the cloud within about 30 s after the cable is back.
8. **Video wall.** Install MediaMTX on the station. Download `mediamtx.yml` (Admin, *Video wall* tab; it contains camera passwords, so keep it on the station) and set `Video__GatewayUrl`. Check: tiles play; set `SilentAlertSeconds` per AI camera once its heartbeat rate is known.
9. **Load check on the real station PC.** On the i3/8 GB PC, stop the site API, then run `python tests/load_test.py --vehicles 100 --lane-gap 9 --bridge-delay <measured controller time>` with the Debug build. PASS means P95 received→open command ≤ 1 s. Record CPU/RAM during the run.
10. **Supervised release per lane.** Continue as in *Supervised release* above: one lane at a time, a guard present. For the car exit lanes, turn on *Inspection photo required* where cargo checks apply.

## Still requires a human/site

Hardware selection and wiring, locally applicable biometric/data-retention decisions, model licensing review, measured accuracy targets, safety commissioning, and supervised pilot sign-off. Software tests cannot substitute for these activities.
