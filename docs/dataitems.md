# CNC12 Agent — Data Item Mapping

Source: `GET http://localhost:5000/CNC_12/current` (MTConnect 1.4, Okuma extension `urn:okuma.com:OkumaStreams:1.4`) and `GET /asset?type=CuttingTool`, captured 2026-10-14 on sender **OKUMA-P241225**, device **CNC12** uuid `CNC12.241225`.

## Streams used by the dashboard

| Need | DataItemId | Name | Kind | Values seen |
|---|---|---|---|---|
| Parts counter | `Mppartcount` | ppartcount | Event | cumulative `PartCount`, resets with program |
| Program / part | `Mpprogram` | pprogram | Event | e.g. `OMV-5733.MIN` |
| Spindle speed | `MS1speed` | S1speed | Sample (RotaryVelocity, ACTUAL) | rpm, 0 = stopped |
| Spindle cmd | `MS1cmd` | S1cmd | Sample (PROGRAMMED) | rpm |
| Execution | `Mpexecution` | pexecution | Event | `ACTIVE`, `READY`, ... |
| Controller mode | `Mpmode` | pmode | Event | `AUTOMATIC`, ... |
| Functional mode | `Mfmode` | fmode | Event | `PRODUCTION`, ... |
| Current tool | `Mp1CurrentTool` | p1CurrentTool | Event | tool number, e.g. `1` |
| Tool asset ref | `Mp1ToolAssetId` | p1ToolAssetId | Event | `123456.1` |
| Accumulated spindle run (power-on) | `Mp1SpindleRunTime` | p1SpindleRunTime | Sample (AccumulatedTime, seconds) | `27012` |
| Accumulated spindle run (lifetime) | `Mp1TotalSpindleRunTime` | — | Sample | `37039149`; also Cutting/Operating/Running variants |
| Availability | `Mavail` | avail | Event | `AVAILABLE` |

## Tool assets (`/asset?type=CuttingTool`)

- `assetId` = `{toolId}.{serialNumber}` (e.g. `123456.14`); stable identity for tracking.
- Life: `<ToolLife countDirection="DOWN" initial="2500" type="PART_COUNT">2450</ToolLife>`
  - Text = remaining; `initial` = budget; values may be `PART_COUNT` or `MINUTES`.
  - `countDirection` UP also possible → value = accumulated use.
- Health: `CutterStatus/Status` = `AVAILABLE` | `BROKEN` (also NEW/USED/EXPIRED per spec).
- Also present: `ProgramToolNumber`, `ProgramToolGroup`, `Location type="POT"` (pocket).

### OK/NG rule
- NG: status `BROKEN` **or** remaining <= 0.
- Warning: remaining < configurable % of initial (e.g. asset 32 = 30/300).
- N/A: no `ToolLife` element (1, 13, 17, 37, 57, 58, 59, 60, 61 in sample) → grey, treated OK.

### Quirks observed
- Asset 18: `BROKEN` with 29 remaining → broken ≠ expired; both must flag NG.
- Asset 20: 26/1250 remaining — life counters reset per program/pocket; track by `assetId`, never tool number alone.
- Asset 36: `ProgramToolNumber=36` but `ProgramToolGroup=16` → group is program slot, number is tool id; use `ProgramToolNumber` for display.

## Not useful (v1)
- `MPlcMonitorIO_*`: all `UNAVAILABLE`.
- `Mp1CommonVariable` (COMMON V1–V200 dump): fallback if `Mppartcount` proves unreliable; V65 mirrored program number 5733.
- Condition blocks (coolant/electric/hydraulic/lube/pneumatic/controller `Normal`): future alarm features.
- `AssetChanged`/`AssetRemoved` events (`assetType=CuttingTool`): trigger immediate `/asset` re-fetch.