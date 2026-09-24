# Barrier bridge contract

The application implements a generic HTTP adapter, not a vendor-specific motor driver. A technician must implement/commission a bridge for the actual controller. Do not connect a relay to the application without the controller's own safety logic.

## Command

Configured server-to-server `POST {Parking:BarrierUrl}/commands` with `Authorization: Bearer <Parking:BarrierKey>`:

```json
{"commandId":"UUID","laneId":"entry-1","action":"Open","expiresUtc":"2026-09-07T09:00:30Z"}
```

The bridge must reject expired commands, persist command IDs across restart, never execute the same ID twice, verify local interlocks, and return:

```json
{"commandId":"same UUID","accepted":true}
```

Acknowledgment means accepted, not that a vehicle passed. Timeouts and malformed acknowledgments are stored as `Unknown`; no automatic command retry occurs. A lane with unknown outcomes is held until an administrator physically checks and reconciles it. A duplicate API request returns the prior command record without re-operating the bridge.

Acknowledged commands without confirmed passage also hold the lane. An administrator can inspect and reconcile a cancelled/uncompleted passage. Per-lane `AutoOpen` is off by default. When enabled, accepted camera observations may request opening through the same gates; blocked automatic requests raise `AUTO_OPEN_HELD` rather than bypassing interlocks.

## Sensor feedback

Every few seconds the bridge sends `POST /api/v1/parking/device/heartbeat` with `X-Device-Key`:

```json
{"laneId":"entry-1","safetyHealthy":true,"vehiclePresent":true,"obstructed":false,"barrierState":"Closed","passageCommandId":null,"tailgating":false}
```

Report passage with the corresponding command ID only when the passage sensor confirms it. Repeat IDs are idempotent. Acknowledged entry/exit passages create/close parking sessions. Tailgating and safety faults produce deduplicated critical alerts. Feedback over 10 seconds old cannot authorize a new live command; after 30 seconds the health worker raises an offline alert.

Only the physical controller lowers the arm after its own safety sensors permit it. The app does not send a blind timed close command. Emergency release and power-loss behavior are installation-specific and must remain functional without this server.

## Enablement gates

All must hold: lane shadow mode off, lane simulation off, server `Parking__EnableLiveBarrier=true`, configured bridge URL/key, recent healthy feedback, vehicle present, barrier closed, no obstruction, unexpired Allow decision, no unresolved/recent lane command. Simulated observations can never operate live hardware.

Use one API process per database/site in this release. The process-wide serialization gate is not a distributed lock. Multi-instance deployment requires a database-backed command reservation protocol and load/fault testing.
