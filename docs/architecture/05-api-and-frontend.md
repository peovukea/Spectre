# API and Frontend Design

## Boundary Principles

- HTTP is an external contract, not a projection of C# domain records.
- Commands acknowledge durable intent and return quickly.
- Queries are bounded, cancelable, versioned, and optimized for the read model.
- Live events are hints with replay; clients always reconcile against query
  endpoints.
- The browser never receives database credentials, server filesystem paths, or
  internal exception details.
- OpenAPI is generated and checked in CI; the TypeScript client is generated
  from the same document.

## API Shape

Use one canonical version prefix, `/api/v1`. Remove duplicate legacy and
pipeline/ingestion aliases once clients migrate.

### Run commands

| Method and route | Purpose | Result |
|---|---|---|
| `POST /api/v1/runs` | Create a run from an allowed input-source ID and analysis profile | `202 Accepted`, run resource and `Location` |
| `POST /api/v1/runs/{runId}/cancel` | Persist cancellation intent | `202 Accepted`; idempotent |
| `POST /api/v1/runs/{runId}/retry` | Retry eligible failed families under explicit policy | `202 Accepted` |

Run creation accepts an `Idempotency-Key` header. Repeating the same key and
equivalent request returns the original run. A conflicting request with the same
key returns a conflict Problem Details response.

### Run queries

| Method and route | Purpose |
|---|---|
| `GET /api/v1/runs` | Cursor-paginated run history |
| `GET /api/v1/runs/{runId}` | Durable state, profile, progress, and terminal reason |
| `GET /api/v1/runs/{runId}/families` | Manifest families and attempt state |
| `GET /api/v1/runs/{runId}/families/{familyId}` | Family progress, attempts, and window range |
| `GET /api/v1/runs/{runId}/events` | Replayable SSE stream scoped to a run |

### Investigation queries

| Method and route | Purpose |
|---|---|
| `GET /api/v1/runs/{runId}/families/{familyId}/windows` | Cursor-paginated summaries |
| `GET /api/v1/runs/{runId}/facets` | Predicates, node kinds, and supported ranges |
| `GET /api/v1/runs/{runId}/families/{familyId}/windows/{window}/graph` | Bounded projection with filters and cursor |
| `GET /api/v1/runs/{runId}/families/{familyId}/windows/{window}/nodes/{nodeId}` | Node details |
| `GET /api/v1/runs/{runId}/families/{familyId}/windows/{window}/interactions/{source}/{target}` | Interaction details and evidence |

Use explicit run IDs. A `latest` convenience alias may redirect to a concrete
run URL, but silently changing the dataset behind a stable browser URL makes
investigations irreproducible.

## Request and Response Rules

### Validation

- Validate syntax at model binding and semantics in the application use case.
- Return RFC Problem Details with stable `type`, `title`, status, trace ID, error
  code, and field errors.
- Use `400` for malformed values, `401` for unauthenticated, `403` for denied,
  `404` for unknown resources, `409` for state/idempotency conflict, `410` for
  intentionally pruned detail, `422` for a valid request that violates domain
  policy when useful, `429` for rate limiting, and `503` for unavailable
  dependencies.
- Never return exception type names or stack traces outside development.

### Pagination and limits

- Cursor tokens are opaque, signed or server-validated, and include the stable
  sort key.
- Every collection has a conservative default and maximum page size.
- Graph limits are enforced in SQL and included in the response.
- Return `hasMore`/next cursor. Exact totals are optional because they can be
  expensive.
- Reject non-finite floating-point filters and unknown facets.

### Concurrency and caching

- Include entity tags or version fields for stable completed resources.
- Completed run summaries may use private/public cache policy appropriate to
  authorization.
- Running resources use `no-store` or short revalidation.
- Command responses are never cached.
- Cancellation and retry commands are idempotent.

### Long integers

Keep Unix nanoseconds, source positions, counters, and database `bigint` IDs as
decimal strings in JSON. OpenAPI must declare the convention consistently. The
generated client should expose a branded `Int64String`, not a JavaScript number.

## Server-Sent Events

SSE remains the correct transport because the server sends one-way progress and
slice notifications and the client issues ordinary HTTP commands.

### Durable event model

- Events are inserted into the PostgreSQL outbox in the same transaction as the
  state they describe.
- Event IDs are database-monotonic and survive API restarts.
- `Last-Event-ID` resumes strictly after the last observed event.
- API replicas can use `LISTEN/NOTIFY` as a wake-up optimization but query the
  outbox for truth.
- Heartbeats keep intermediaries from timing out idle connections.
- Slow clients have bounded per-connection buffers. If a client falls behind,
  close the stream with a reconnect hint rather than dropping events silently.
- Event payloads are small and versioned. Large graph data remains query-only.

Recommended event types:

- `run-state-changed`;
- `run-progressed`;
- `family-attempt-started`;
- `family-promoted`;
- `slice-promoted`;
- `retention-changed`;
- `run-terminal`.

On connect and reconnect, the frontend first fetches the run snapshot, then
opens the stream from its last event ID, and reconciles affected resources after
events. This avoids relying on event delivery for correctness.

## Authentication and Authorization

Use the organization's OpenID Connect/OAuth 2.0 provider. Prefer same-origin,
HTTP-only secure cookies at the Next.js BFF when browser deployment permits.

Initial roles/policies:

- `Spectre.Reader`: view allowed runs and evidence;
- `Spectre.Operator`: create, cancel, and retry runs;
- `Spectre.Admin`: register input sources, change retention, and manage system
  configuration.

Authorization is resource-aware if runs belong to tenants, teams, or cases.
Audit every control action with actor, time, request ID, run ID, result, and
reason.

## OpenAPI and Contract Evolution

- Generate one reviewed OpenAPI artifact per supported major API version.
- Fail CI on unreviewed breaking changes.
- Generate TypeScript types and client functions; do not manually duplicate all
  DTOs.
- Add runtime boundary validation for external/untrusted responses if the
  generated client does not provide it.
- Add consumer contract tests for 64-bit strings, enums, nullable fields,
  Problem Details, and SSE event schemas.
- Use additive fields for compatible changes and a new major version for
  semantic or shape breaks.
- Keep domain and persistence types internal so their evolution does not force
  API versions.

## Next.js Application Architecture

The current page is statically rendered but immediately hands the entire
dashboard to one Client Component. The target should use the App Router's
default Server Component model and add client boundaries only where browser
state or browser APIs are required.

### Route structure

```text
app/
  layout
  loading
  error
  runs/
    page
    [runId]/
      layout
      page
      loading
      error
      families/
        [familyId]/
          windows/
            [windowStart]/page
```

The URL identifies run, family, window, and shareable filters. Browser back,
refresh, bookmarks, and incident links must preserve investigation context.

### Server Components

Use Server Components for:

- authenticated initial run/family/window data;
- stable completed-run summaries;
- page layout and non-interactive detail;
- access checks and redirect to canonical concrete run IDs;
- streaming initial sections under Suspense boundaries.

Server Components should call the ASP.NET API directly, not a Next Route Handler
that then calls the same API, because the extra internal HTTP hop adds no value.
Use `no-store` for active run data, and explicit revalidation/tag invalidation for
completed data.

### Client Components

Use small Client Components for:

- SSE lifecycle and reconciliation;
- start/cancel/retry controls;
- graph rendering with Sigma/Graphology;
- interactive filters and selection;
- local viewport, hover, and zoom state;
- accessible dialogs, drawers, and keyboard commands.

Keep API data state separate from ephemeral graph-renderer state. A reducer or
state machine should make connection, loading, stale, error, and selection
transitions explicit rather than coordinating many independent state variables.

### BFF and proxy boundary

Use Next Route Handlers only where they add a real browser boundary:

- exchange/forward secure authentication without exposing tokens;
- enforce same-origin browser access;
- attach correlation and anti-forgery context;
- normalize deployment-specific API origin.

A development rewrite is acceptable locally. In production, make the BFF or
direct API boundary explicit and test it. Do not rely on permissive CORS as the
authentication design.

### Loading and errors

- Route-level `loading` UI provides immediate navigation feedback.
- Segment-level error boundaries distinguish API unavailable, unauthorized,
  pruned detail, invalid filters, and renderer failure.
- Abort stale fetches when run/family/window changes.
- Preserve the last good graph with a stale indicator during refresh where
  doing so avoids disruptive blanking.
- Retrying an idempotent query is user-visible and bounded.

### Graph performance

- Keep server-enforced graph budgets; the browser is not the primary limit.
- Build graph structures once per projection, not on every hover/render.
- Move expensive layout to a Web Worker if profiling shows main-thread stalls.
- Prefer progressive detail: summary, bounded projection, then node/edge detail.
- Virtualize long run/window/filter lists.
- Avoid serializing duplicate large maps through Server-to-Client props.
- Record render time, layout time, node/edge count, and client errors.

### Accessibility

Canvas/WebGL graph exploration requires a parallel accessible representation:

- keyboard-operable node and edge selection;
- visible focus and non-color significance cues;
- screen-reader summary and tabular selected results;
- labeled controls and live regions for run state;
- reduced-motion behavior;
- adequate contrast and scalable text;
- no essential action available only by hover.

## Frontend Testing

- unit-test formatting, reducers, URL/filter parsing, and API error mapping;
- component-test control states and accessible detail views;
- contract-test generated types against the OpenAPI artifact;
- Playwright-test start/cancel authorization, run navigation, reconnect,
  pruned-detail handling, filters, keyboard selection, and error boundaries;
- load-test large allowed graph projections on representative hardware;
- run accessibility automation plus manual keyboard/screen-reader checks.
