# Spectre Investigation Frontend

This is the Next.js investigation UI for the PostgreSQL-backed
`Spectre.InvestigationHost` API. It renders run status, memory pressure, family
windows, retained graph projections, and node/interaction detail for the latest
or selected historical disparity-filtered backbone.

## Run Locally

Start the API:

```powershell
docker-compose -f ..\compose.yml up -d postgres
dotnet run --project ..\Spectre.InvestigationHost --configuration Release
```

Start ingestion from the dashboard header, or call the API explicitly after the
API is listening:

```powershell
Invoke-RestMethod `
  -Method Post `
  -ContentType "application/json" `
  -Body '{"inputPath":"d:\\Proj\\data\\cadets"}' `
  http://localhost:5143/api/ingestion/start
```

Start the frontend:

```powershell
npm run dev
```

Open `http://localhost:3000/investigate`.

The header controls call `POST /api/ingestion/start` and
`POST /api/ingestion/cancel` through the frontend proxy. The Start button uses
the backend's configured `InputPath`; use the explicit API call when you need to
override it for a run.

Use the Investigation run selector in the left panel to inspect previous
persisted runs. Historical views are read-only; switch back to `Latest run` to
use the Start and Cancel buttons.

## Scripts

```powershell
npm run lint
npm run build
npm run start
```

`npm run build` runs the production Next.js build and TypeScript check.

## API Contract Notes

- The backend streams live updates from `GET /api/events` using Server-Sent
  Events. Clients reconcile reconnects by refetching status, memory, families,
  and windows; the stream does not replay old events.
- Graph windows can return `410 Gone` when their detailed rows have been pruned.
  The PostgreSQL-backed host persists detailed windows by default.
- Unknown windows and unknown detail records return `404`.
- All backend `Int64` fields are JSON strings. Use the `Int64String` type in
  `lib/contracts.ts` and parse only at display boundaries.
- Valid graph query limits are `minWeight >= 0`, `maxNodes` in `2..1000`, and
  `maxEdges` in `1..2000`.
- `GET /api/runs` lists persisted runs. Most read routes accept optional
  `runId`; omitting it follows the newest run.
