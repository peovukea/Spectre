# Spectre Investigation Frontend

This is the Next.js investigation UI for the in-memory
`Spectre.InvestigationHost` API. It renders run status, memory pressure, family
windows, retained graph projections, and node/interaction detail for the live
disparity-filtered backbone.

## Run Locally

Start the API:

```powershell
dotnet run --project ..\Spectre.InvestigationHost --configuration Release -- `
  --InputPath d:\Proj\data\cadets
```

Start the frontend:

```powershell
npm run dev
```

Open `http://localhost:3000/investigate`.

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
- Graph windows can return `410 Gone` when their detailed slice or default
  projection has been evicted by bounded retention.
- Unknown windows and unknown detail records return `404`.
- All backend `Int64` fields are JSON strings. Use the `Int64String` type in
  `lib/contracts.ts` and parse only at display boundaries.
- Valid graph query limits are `minWeight >= 0`, `maxNodes` in `2..1000`, and
  `maxEdges` in `1..2000`.
