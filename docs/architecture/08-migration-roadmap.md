# Migration Roadmap

## Migration Principle

Do not replace the current system with an unverified big-bang rewrite. Treat the
existing implementation as a behavioral oracle where its semantics are
intentional, freeze those results, then replace one boundary at a time behind
executable equivalence and operational gates.

The target may be built in a new solution tree, but cutover remains incremental.

## Phase 0: Define the Contract of Correctness

### Work

- inventory current public HTTP routes and persisted result shapes;
- create canonical input fixtures covering every supported CDM datum and edge
  case;
- capture current family/window/document/interaction results and hashes;
- document the 12 semantic decisions listed in the current-state assessment;
- capture the full reference manifest, hardware, throughput, memory, and result
  counts;
- classify known bugs separately from compatibility requirements;
- decide which current behavior becomes analysis profile v1.

### Exit gate

- golden fixtures are human-reviewed;
- repeated current runs are deterministic;
- baseline counts/hashes and performance report are stored;
- every intentional semantic change has an ADR and a new expected profile.

## Phase 1: Establish the Engineering Baseline

### Work

- add pinned SDK and Node versions;
- centralize .NET build and package configuration;
- add repository editor/analyzer policy;
- make hand-authored code warning-clean;
- isolate generated Avro warnings and regeneration;
- add architecture tests and CI workflow stages;
- make root documentation and commands accurate;
- add dependency, secret, and license scanning.

### Exit gate

- one command builds and tests backend and frontend from a clean checkout;
- CI is warning-clean and reproducible;
- generated artifacts either reproduce cleanly or fail with a clear diff;
- existing behavior remains unchanged.

## Phase 2: Build Domain and Analysis Core

### Work

- create infrastructure-free domain records and strong identifiers;
- move projection, semantic indexing, and disparity math into `Spectre.Analysis`;
- replace mutable public metrics with internal recorders and immutable snapshots;
- express explicit family start, batch processing, successful completion, and
  failure rather than disposal-driven business transitions;
- version the analysis profile and canonical result hashing;
- port current unit tests and add property/golden tests.

### Exit gate

- Domain and Analysis have no infrastructure/framework dependencies;
- profile v1 golden outputs match the current oracle;
- all numerical and ordering invariants pass;
- component benchmark is no slower than the current analysis-only baseline or
  has an approved measured explanation.

## Phase 3: Replace the Avro Boundary

### Work

- create the allowlisted input-source catalog and immutable manifest model;
- isolate generated CDM18 records and Apache Avro;
- implement explicit/static normalization maps;
- classify input errors and bound external values;
- preserve sync-block evidence semantics;
- add manifest fingerprints and change detection;
- run Avro integration and fuzz/corruption cases.

### Exit gate

- normalized datum golden stream matches the current reader for supported data;
- malformed/unsupported/fatal classifications are explicit;
- no per-record reflection remains on the supported hot path unless a benchmark
  proves it preferable;
- read/normalize throughput meets or exceeds baseline with bounded memory.

## Phase 4: Introduce Durable Runs and Family Attempts

### Work

- add new run, manifest, family-job, attempt, and outbox tables alongside current
  tables;
- implement idempotent run creation and durable cancellation;
- implement claim, lease, heartbeat, fencing, abandonment, and retry policy;
- build worker orchestration for one synchronous family lane;
- write attempt-scoped progress and terminal reasons;
- add forced-kill and concurrent-claim tests.

### Exit gate

- API restart loses no accepted job;
- worker kill causes safe replay after lease expiry;
- stale workers cannot promote;
- cancellation is durable and observed within target;
- no partial attempt is visible as a successful family.

## Phase 5: Build Attempt-Scoped Result Persistence

### Work

- add attempt identity to new summary/document/interaction tables;
- retain binary COPY with bounded transactions;
- implement active-attempt promotion and failed-attempt cleanup;
- add filter dimension tables and result hashes;
- build read repositories returning application query models, not HTTP DTOs;
- validate indexes and query plans with reference-size data.

### Exit gate

- crash at every tested write boundary exposes either the old promoted attempt
  or the complete new attempt, never a mixture;
- replay produces the same content hashes;
- graph queries meet latency and work bounds;
- old and new stores can run side by side for comparison.

## Phase 6: Separate API and Worker Roles

### Work

- create independent API and worker entry points;
- remove analysis and `Task.Run` orchestration from HTTP process;
- add role-specific options, connection pools, health checks, graceful shutdown,
  OpenTelemetry, and dashboards;
- run multiple API replicas and at least two test workers to prove lease safety;
- keep worker concurrency at one until the equivalence/performance gate passes.

### Exit gate

- API remains responsive during full reference ingestion;
- API and worker can restart independently;
- readiness/liveness behavior is verified;
- operational dashboards and primary runbooks exist;
- full run meets correctness and baseline throughput.

## Phase 7: Deliver API v1 and Durable Events

### Work

- implement canonical `/api/v1/runs/...` command and query resources;
- add OIDC authentication, authorization policies, audit, Problem Details,
  pagination, rate limits, and request cancellation;
- generate OpenAPI and TypeScript client;
- implement PostgreSQL outbox-backed SSE replay;
- provide a temporary compatibility facade for current routes if required;
- add functional, contract, security, and SSE reconnect tests.

### Exit gate

- all endpoints have explicit auth and limits;
- contract compatibility checks pass;
- SSE resumes across API restart without silent loss;
- current frontend can operate through compatibility endpoints or an adapter;
- no arbitrary physical input path is accepted remotely.

## Phase 8: Rebuild the Next.js Investigation UI

### Work

- introduce route-addressable run/family/window pages;
- fetch initial views in Server Components;
- isolate SSE, graph, filters, and controls into Client Components;
- use generated API contracts;
- implement route loading/error boundaries and stale-data behavior;
- provide accessible graph alternatives and keyboard flows;
- add Playwright, component, accessibility, and large-graph performance tests.

### Exit gate

- deep links and refresh preserve investigation context;
- live reconnection is deterministic;
- completed-run views use deliberate caching and active runs do not;
- browser tests cover operator and reader workflows;
- lint, type, build, accessibility, and performance gates pass.

## Phase 9: Performance and Scale Hardening

### Work

- profile full pipeline and database under the reference workload;
- tune batches, buffers, COPY, queries, and connection budgets;
- introduce bounded family parallelism one step at a time;
- test API latency while workers saturate storage/CPU;
- run soak, cancellation, replay, backup/restore, and retention tests;
- establish final SLOs, alerts, capacity model, and cost baseline.

### Exit gate

- full reference hashes match profile v1;
- throughput and memory budgets pass repeatedly;
- API SLOs pass during ingestion;
- recovery and restore objectives are demonstrated;
- alert/runbook drills succeed.

## Phase 10: Parallel Production Validation and Cutover

### Shadow mode

For selected immutable inputs:

1. current system remains authoritative;
2. new worker processes the same manifest under a shadow run;
3. compare family/window counts, summaries, detailed hashes, metrics, duration,
   and resource use;
4. investigate every unexplained difference;
5. repeat across representative datasets and failure scenarios.

### Cutover

- freeze incompatible schema changes during the window;
- verify backup and rollback;
- deploy migration, API, worker, then web;
- route new commands to the durable run system;
- keep old read routes temporarily if required;
- monitor correctness hashes, attempts, queue lag, API latency, database pressure,
  and client errors;
- remove compatibility paths only after an agreed observation period.

### Rollback

Rollback must not require deleting new data. Expand/contract schemas allow the
old read path to remain available. Stop new worker claims, route commands back
only if the old system can safely accept them, and preserve new run manifests
and attempts for diagnosis.

## Workstream Dependencies

```text
Correctness freeze
  +-> engineering baseline
  +-> domain/analysis core
          +-> Avro boundary
          +-> durable run/attempt model
                  +-> result persistence
                          +-> API/worker split
                                  +-> API/events
                                          +-> frontend
                                                  +-> scale hardening
                                                          +-> cutover
```

Some work can overlap, but no downstream phase may declare success without its
upstream correctness and durability gates.

## Risk Register

| Risk | Mitigation |
|---|---|
| Rewrite changes analytical meaning | Golden fixtures, versioned profiles, hashes, shadow runs |
| Family replay is too expensive | Measure first; add versioned state snapshots only if required |
| PostgreSQL becomes queue/write/query bottleneck | Separate pool budgets, bounded lanes, COPY, query plans, capacity tests |
| Parallelism changes deterministic order | Manifest ordinals, ordered lanes, canonical hashes, concurrency-variance tests |
| New schema doubles storage during migration | Capacity plan, short failed-attempt retention, staged cleanup |
| API contract migration breaks frontend | OpenAPI generation, compatibility facade, consumer tests |
| SSE replay table grows | Retention by event age/terminal run with reconnect window |
| Input source changes between shadow runs | Immutable manifests and fingerprints |
| Security work is deferred | Make auth, input allowlisting, and secret removal API exit gates |
| Performance focus weakens validation | Correctness and bounds remain non-negotiable; optimize from profiles |

## Definition of Done

The rebuild is complete only when:

- all documented profile v1 semantic behavior is proven by golden tests;
- full reference result hashes are equivalent or approved under a new profile;
- accepted jobs, cancellation, attempts, and events survive process restarts;
- failed/replayed attempts cannot leak partial results;
- API and worker scale/restart independently;
- query, throughput, memory, and recovery budgets pass;
- authentication, authorization, allowlisted inputs, audit, and secrets policy are
  active;
- OpenAPI and frontend contracts are generated and tested;
- observability, alerts, backups, restores, and runbooks are exercised;
- legacy routes, stores, duplicate contracts, and in-memory run orchestration are
  removed;
- architecture documents and ADRs match the deployed system.
