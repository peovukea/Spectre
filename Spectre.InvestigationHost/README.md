# Investigation Host

`Spectre.InvestigationHost` is an ASP.NET Core API for live inspection of the
in-memory disparity-filtered backbone produced by the ingestion pipeline. It is
bounded by design: summaries are retained for all observed slices, while
detailed slices and default graph projections are kept only within configured
memory/count limits.

## Run

```powershell
dotnet run --project Spectre.InvestigationHost --configuration Release -- `
  --InputPath d:\Proj\data\cadets
```

The frontend expects the API at `http://localhost:5000` unless configured
otherwise. CORS is currently open to `http://localhost:3000` for local Next.js
development.

## HTTP API

| Route | Notes |
|---|---|
| `GET /api/status` | Current run state, partial flag, elapsed seconds, and latest metrics. |
| `GET /api/memory` | Retention counts, estimated retained bytes, process memory, and eviction counters. |
| `GET /api/families` | Observed input families. During active ingestion, response is `Cache-Control: no-store`. |
| `GET /api/families/{familyId}/windows` | Slice summaries for one family. |
| `GET /api/predicates` | Observed predicates for graph filtering. |
| `GET /api/node-kinds` | Observed node kinds for graph filtering. |
| `GET /api/families/{familyId}/windows/{windowStart}/graph` | Bounded graph projection. |
| `GET /api/families/{familyId}/windows/{windowStart}/nodes/{nodeId}` | Detailed node terms and weights. |
| `GET /api/families/{familyId}/windows/{windowStart}/interactions/{source}/{target}` | Detailed interaction breakdown and evidence. |

Graph query parameters:

| Parameter | Default | Limit |
|---|---:|---|
| `minWeight` | `0` | finite number, `>= 0` |
| `maxNodes` | `250` | `2..1000` |
| `maxEdges` | `200` | `1..2000` |
| `predicate` | unset | must be an observed predicate when supplied |
| `nodeKind` | unset | must be an observed node kind when supplied |

Unknown families/windows return `404`. Windows retained only as summaries, or
projection-retained windows requested with non-default graph filters, return
`410 Gone`.

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
