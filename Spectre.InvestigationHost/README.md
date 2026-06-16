# Investigation Host

`Spectre.InvestigationHost` is an ASP.NET Core API for live inspection of the
PostgreSQL-persisted disparity-filtered backbone produced by the ingestion
pipeline. The API defaults to the latest run and also supports read-only
historical run inspection with `runId` query parameters.

## Run

Start the local PostgreSQL database:

```powershell
docker-compose up -d postgres
```

The default development connection string points to this compose service:

```text
Host=localhost;Port=55432;Database=spectre;Username=spectre;Password=spectre_dev_password
```

```powershell
dotnet run --project Spectre.InvestigationHost --configuration Release
```

Starting the API does not start ingestion. Trigger ingestion explicitly:

```powershell
Invoke-RestMethod `
  -Method Post `
  -ContentType "application/json" `
  -Body '{"inputPath":"d:\\Proj\\data\\cadets"}' `
  http://localhost:5143/api/ingestion/start
```

Cancel an active run:

```powershell
Invoke-RestMethod -Method Post http://localhost:5143/api/ingestion/cancel
```

If the host is stopped while a run is active, the next startup marks the old
persisted `Running` row as `Failed`/partial. A run is only active when started
by this API process.

The frontend expects the API at `http://localhost:5000` unless configured
otherwise. CORS is currently open to `http://localhost:3000` for local Next.js
development.

Set `ConnectionStrings:InvestigationStore` to use a different PostgreSQL
instance. `Database:ApplyMigrationsOnStartup` defaults to `true`, so the host
applies the investigation-store EF migration at startup.

## HTTP API

| Route | Notes |
|---|---|
| `GET /api/runs` | Recent persisted runs, newest first, with family/window counts. |
| `GET /api/status` | Current run state, partial flag, elapsed seconds, and latest metrics. Accepts optional `runId`. |
| `POST /api/ingestion/start` | Starts ingestion. Optional JSON body: `{ "inputPath": "d:\\Proj\\data\\cadets" }`. |
| `POST /api/ingestion/cancel` | Requests cancellation for the active ingestion run. |
| `GET /api/memory` | Retention counts, estimated retained bytes, process memory, and eviction counters. |
| `GET /api/families` | Observed input families. Accepts optional `runId`. During active ingestion, response is `Cache-Control: no-store`. |
| `GET /api/families/{familyId}/windows` | Slice summaries for one family. Accepts optional `runId`. |
| `GET /api/predicates` | Observed predicates for graph filtering. Accepts optional `runId`. |
| `GET /api/node-kinds` | Observed node kinds for graph filtering. Accepts optional `runId`. |
| `GET /api/families/{familyId}/windows/{windowStart}/graph` | Bounded graph projection. Accepts optional `runId`. |
| `GET /api/families/{familyId}/windows/{windowStart}/nodes/{nodeId}` | Detailed node terms and weights. Accepts optional `runId`. |
| `GET /api/families/{familyId}/windows/{windowStart}/interactions/{source}/{target}` | Detailed interaction breakdown and evidence. Accepts optional `runId`. |

When `runId` is omitted, read routes resolve against the newest run. Historical
runs are read-only; ingestion start/cancel always target the active API process.

Graph query parameters:

| Parameter | Default | Limit |
|---|---:|---|
| `minWeight` | `0` | finite number, `>= 0` |
| `maxNodes` | `250` | `2..1000` |
| `maxEdges` | `200` | `1..2000` |
| `predicate` | unset | must be an observed predicate when supplied |
| `nodeKind` | unset | must be an observed node kind when supplied |

Unknown families/windows return `404`. A future pruned-detail summary can return
`410 Gone`, but the PostgreSQL store persists detailed windows by default.

## Server-Sent Events

`GET /api/events` streams new events only. `Last-Event-ID` is accepted but not
replayed; clients should reconcile by fetching `/api/status`, `/api/memory`, and
the family/window endpoints after reconnecting.

Event frames include a server-assigned monotonic `id`, an `event` name, and JSON
`data`. Heartbeats are sent every 15 seconds as SSE comments.

Current event types:

| Event | Payload |
|---|---|
| `run-status` | `RunStatusDto` |
| `slice-closed` | `SliceSummaryDto` |
| `memory-pressure` | `StoreMemoryPressureDto` |
| `retention-changed` | `{ familyId, windowStartNanos, newLevel }` |

## JSON Contract

All `long`/`Int64` values are serialized as JSON strings so JavaScript clients do
not lose precision beyond `Number.MAX_SAFE_INTEGER`. The frontend models these
fields as `Int64String` and parses them only for display.
