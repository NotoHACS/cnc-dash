# cnc-dash

CNC performance dashboard for Okuma machines, fed from the machine's MTConnect
agent, with an admin-configurable metrics model.

```
[CNC (Okuma THINC, Win10)]                 [LXC on Proxmox]
  mtconnect-agent :5000                      cncdash (ASP.NET Core 8 + SQLite)
       │ /current @2s + /asset @60s                ▲
  collector.exe (C# service) ── HTTPS POST ──── cloudflared │ cnc-dash.example.com
       └─ self-update: update.bat → GitHub releases
```

- **Collector** (`/collector`) — polls the machine's MTConnect agent, sends only
  changed values, buffers to disk when the server is unreachable, self-updates via
  `update.bat` from GitHub releases (mirrors the mtconnect-alarm-ntfy flow).
- **Server** (`/server`) — ingest API (API-key auth, idempotent by data item +
  sequence), metrics engine (shift windows, cycle-time learning, RYG status,
  spindle uptime, tool OK/NG), Razor + htmx dashboard, admin UI.
- Data item mapping and machine quirks: `docs/dataitems.md`
- Deployment: server deploy scripts live here (`deploy/publish.sh`,
  `deploy/cncdash.service`); the LXC/tunnel provisioning note is private
  (companion repo `cnc-dash-deploy`). Point `ServerUrl` in collector config at
  your own hostname.

## URLs

| Path | What |
|---|---|
| `/dashboard` | live dashboard (auto-refresh 5s) |
| `/dashboard/admin` | config: shift times, part name, parts/cycle, thresholds, machines + API keys |
| `/api/status/{machine}` | machine status JSON |
| `POST /api/ingest/{machine}` | collector ingest endpoint (X-Api-Key) |
| `/healthz` | health check |

## Status colors

- **RED** — spindle stopped
- **YELLOW** — spindle running, parts made < green-threshold % of expected
  (expected = theoretical max rate × elapsed shift time)
- **GREEN** — running and ≥ green-threshold % (default 75)
- **LEARNING** — spindle running but not enough cycles collected yet to know the
  theoretical max
- **OFF** — no shift running or collector data stale

Theoretical max rate = `parts per cycle / learned cycle time` where cycle time is
the rolling median of the last N (default 20) part-count increments. Seed with an
operator-estimated cycle in admin if you want a target before learning completes.

Shift windows may overlap (defaults here: 6:00–14:30 / 14:00–22:30 / 22:00–6:30);
a part is counted into **every** shift whose window contains its timestamp.
Parts counted at the increment timestamp; counter resets (program restarts) are
handled.

Tool grading: NG when `CutterStatus` is BROKEN or remaining life ≤ 0; WARN below
% threshold; N/A when the machine has no life data for that tool.

## Local dev

```
dotnet run --project server        # ASPNETCORE_URLS=http://localhost:5154
CNC_DASH_SEED_MACHINE=cnc12        # on first run: creates machine + prints API key
dotnet run --project collector     # needs a mock or real agent at localhost:5000
```

Env vars: `CNC_DASH_DB` (sqlite path), `CNC_DASH_ADMIN_PASSWORD` (default
`changeme` — set it in production), `CNC_DASH_SEED_MACHINE` (first-run only).

## Collector install on the CNC (Windows)

1. Download the latest `collector-win-x64.zip` from GitHub releases (or build
   locally), extract to e.g. `D:\cncdash\collector`
2. Create `config_local.json`:
   ```json
   { "ApiKey": "<key from /dashboard/admin>",
     "ServerUrl": "https://cnc-dash.example.com" }
   ```
3. Install as a service: `nssm install cncdash-collector D:\cncdash\collector\collector.exe`
4. (optional self-update) Task Scheduler or manual run of `update.bat` — set
   `CNC_UPDATE_REPO=<owner>/<repo>` env or edit update.bat, then it downloads
   releases, preserves `config_local.json`/`state.json`, restarts the service,
   and rolls back on failure.

## Repo layout

```
collector/   C# collector + config_default.json + update.bat/ps1
server/      ASP.NET Core 8 (Data, Services, Api, Pages)
deploy/      systemd unit, publish.sh, hermes LXC prompt
docs/        MTConnect data-item mapping (discovered from CNC12)
```
