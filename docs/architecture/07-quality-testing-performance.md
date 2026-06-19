# Quality, Testing, and Performance

## Repository Standards

### Central build configuration

Add repository-level policy rather than repeating it in every project:

- pinned .NET SDK in `global.json`;
- `Directory.Build.props` for target framework, nullable, implicit usings,
  deterministic builds, analyzers, documentation policy, and warnings;
- `Directory.Packages.props` for central package management;
- `.editorconfig` for C# and TypeScript formatting/naming rules;
- lock files or locked restore in CI;
- common test settings and coverage exclusions for generated Avro code;
- generated-code markers and a dedicated warning policy for generated files.

Hand-authored projects build with warnings as errors. Do not globally suppress
missing public documentation while enabling XML docs. Either document the
intentionally public API or keep the type internal.

### Code rules

- Nullable reference types remain enabled.
- Public API is minimal and reviewed.
- No catch-all exception swallowing in core or infrastructure.
- No sync-over-async (`Result`, `Wait`) on request paths.
- No `Task.Run` for durable work initiated by HTTP.
- Cancellation flows through waiting I/O and bounded queues.
- No unbounded channel, collection, response, retry, or log sample.
- All numeric accumulation that can overflow is checked; floating values must be
  finite where domain rules require it.
- Culture-sensitive parsing/formatting is explicit.
- Domain state transitions are centralized and tested.
- SQL is parameterized and located in the PostgreSQL adapter.
- Generated files are regenerated, never manually edited.

### Architecture enforcement

Automated architecture tests should prove:

- Domain references no other solution project or infrastructure package.
- Application references Domain only.
- Analysis references Domain and no infrastructure namespace.
- API endpoint modules contain no Npgsql, Apache Avro, or analysis concrete
  types.
- Worker composition may reference adapters; worker use cases do not.
- HTTP contracts do not reference persistence or generated Avro types.
- no implementation project duplicates a contract source file;
- only composition roots call adapter registration entry points;
- public types stay within approved namespaces.

## Test Strategy

Tests are organized by the failure they catch, not only by class name.

### Domain and algorithm unit tests

Fast tests with no filesystem, network, database, wall clock, or framework host:

- run and attempt state-transition tables;
- window alignment at zero, negative timestamps, and integer boundaries;
- watermark and late-data policy;
- TF, IDF, and Jaccard formula examples;
- rolling corpus and previous-self semantics;
- self-edge behavior;
- interaction consolidation and evidence ordering;
- disparity degree-one, alpha boundary, zero/invalid/overflow cases;
- deterministic sort and content hashing;
- query and pagination value-object validation.

Use data-driven and property-based tests for mathematical invariants:

- Jaccard is within `[0,1]`, symmetric, and one for equal sets;
- normalized weight is within `[0,1]` for valid populations;
- retained edges are a subset of candidates;
- reduction counts and weights reconcile;
- ordering and hashes are independent of dictionary insertion order;
- retrying/promotion does not expose two active attempts.

### Golden semantic tests

Before changing architecture, freeze representative current outputs:

- one event with each predicate-object combination;
- supported entity kinds and unknown subtype fallback;
- missing/malformed/unsupported records;
- multiple segments and family boundaries;
- out-of-order event time around watermark thresholds;
- repeated windows showing rolling TF-IDF and Jaccard baselines;
- predicate consolidation and both disparity directions;
- evidence caps and ordering.

Store canonical, human-reviewable expected summaries and hashes. A changed hash
requires either a bug explanation or a new analysis-profile version.

### Avro integration tests

Use real small object-container fixtures and the actual Apache Avro adapter:

- valid CDM18 specific records;
- wrong writer schema;
- empty, truncated, and corrupt containers;
- large/invalid property and collection limits;
- contiguous multi-segment family order;
- cancellation during enumeration;
- sync-block provenance semantics;
- generated-record regeneration compatibility;
- input file changed after manifest creation.

Do not mock the Avro library for these cases.

### PostgreSQL integration tests

Run against the same PostgreSQL major version as production:

- migrations from empty and previous supported schemas;
- concurrent job claiming and `SKIP LOCKED` behavior;
- lease renewal, expiry, fencing, and stale-worker rejection;
- attempt isolation and atomic promotion;
- crash between slice writes and promotion;
- idempotency-key conflict;
- binary COPY for empty and large slices;
- query filters, stable ordering, cursors, limits, and cancellation;
- retention transitions and `Gone` behavior;
- outbox insert/replay/pruning;
- cleanup of failed attempts;
- real query plans on representative cardinality.

Use disposable database instances/schemas. Tests that claim PostgreSQL behavior
must not substitute an in-memory provider.

### Application tests

Use fakes for ports to verify use-case policy:

- run creation and manifest failure;
- cancel before claim, during attempt, and after terminal state;
- transient versus permanent retry classification;
- family completion aggregation;
- terminal run decision with mixed family outcomes;
- promotion and outbox ordering;
- authorization-independent business rules.

### API functional tests

Host the real ASP.NET pipeline with `WebApplicationFactory` and a real test
database for critical flows:

- authentication and role policies;
- command idempotency and conflicts;
- Problem Details shapes;
- pagination and graph limits;
- 64-bit string serialization;
- unknown versus pruned resources;
- request cancellation reaching Npgsql;
- rate limiting;
- OpenAPI generation;
- SSE replay from `Last-Event-ID`, reconnect, and slow-client behavior;
- API remains responsive while worker writes representative slices.

### Frontend tests

- unit: formatting, filter parsing, reducers/state machines, contract errors;
- component: run controls, loading/stale/error states, detail views, accessibility;
- end-to-end: authentication, start/cancel, run selection, deep links, SSE
  reconnect, pruning, graph filters, keyboard navigation;
- visual checks only for stable high-value views, not every pixel;
- generated client drift check against OpenAPI.

### Fault-injection tests

Automate failures at important boundaries:

- kill worker during read, window accumulation, COPY, and pre-promotion;
- expire a lease while the old worker continues;
- restart API during SSE delivery;
- drop database connections and exhaust the pool;
- make disk/database quota fail;
- request cancellation under queue saturation;
- corrupt one family while others are valid;
- change source metadata after validation.

The assertions focus on visibility, replay, terminal reason, and lack of
duplicate promoted results.

## Test Pyramid and CI Frequency

| Suite | Pull request | Main/nightly | Release |
|---|---|---|---|
| Formatting, analyzers, architecture | Yes | Yes | Yes |
| Domain/application unit | Yes | Yes | Yes |
| Golden semantic | Yes | Yes | Yes |
| Avro/PostgreSQL integration | Yes, focused | Full | Full |
| API functional | Yes | Full | Full |
| Frontend lint/type/unit | Yes | Yes | Yes |
| Browser end-to-end | Smoke | Full | Full |
| Fault injection | Selected | Full | Full |
| Microbenchmarks | Regression subset | Full trend | Full |
| Reference/full-data benchmark | No | Scheduled | Required before performance-sensitive release |
| Security/dependency scan | Yes | Yes | Yes |
| Backup restore/migration rehearsal | No | Scheduled | Required for schema release |

## Performance Engineering Method

### Establish reproducible baselines

Record:

- commit, .NET runtime, package versions, OS, CPU, memory, disk, PostgreSQL
  version/configuration, and dataset manifest hash;
- cold and warm filesystem-cache runs separately;
- worker lane count and all queue/batch settings;
- wall time, bytes/records/facts per second;
- peak working set, GC heap/allocation/pause metrics;
- database rows/bytes per second, WAL, and query latency;
- result hashes and counts.

Never compare performance numbers from different result semantics or manifests.

### Benchmark layers

#### Microbenchmarks

Measure only proven hot operations:

- UUID conversion;
- static normalization maps versus reflection;
- predicate normalization;
- window assignment;
- term extraction and counting;
- Jaccard and disparity scoring;
- canonical ordering/hashing;
- JSON serialization of bounded detail;
- batch/channel overhead.

Use microbenchmarks to explain profiles, not to predict full-pipeline throughput.

#### Component benchmarks

- Avro read/normalize to a null consumer;
- facts through semantic indexing to a null slice consumer;
- slices through disparity filtering;
- slice bulk write to PostgreSQL;
- representative graph queries;
- Next.js graph preparation/layout/render.

#### End-to-end benchmarks

Maintain small, medium, and full reference manifests. The full benchmark must
verify result hashes in addition to speed.

## Performance Budget

Initial budgets, to be calibrated on named hardware:

| Area | Budget/gate |
|---|---|
| End-to-end throughput | No regression from current roughly 88,500 source records/s reference baseline |
| Peak worker memory | Flat with input size; initial 2 GiB per family lane ceiling |
| Queue memory | Explicit fraction of worker budget; no unbounded growth |
| Allocation | Trended per million records; no unexplained release regression |
| PostgreSQL copy | Sustains worker output without prolonged queue saturation |
| Graph API | P95 below 500 ms for bounded reference queries |
| API under ingestion | P95 degradation stays within agreed percentage |
| Frontend graph | Interactive after bounded projection; no long main-thread task above agreed threshold |

The current nine-minute full-data result is a useful baseline but must be
rerun under a scripted benchmark before it becomes a release gate.

## Optimization Priorities

Optimize in this order after profiling:

1. remove per-record reflection with generated/static normalizers;
2. reduce per-record allocations and repeated strings in term/predicate handling;
3. batch datum/fact transfer while preserving order;
4. tune file buffers and sequential read behavior;
5. tune semantic accumulator dictionaries and output-boundary sorting;
6. tune binary COPY chunking, indexes, transactions, and connection budgets;
7. add bounded family parallelism;
8. parallelize independent closed slices if still useful;
9. consider pooling or specialized collections only with demonstrated gain.

Avoid these premature optimizations:

- unsafe code without a measured bottleneck;
- broad parallel record processing that changes ordering;
- persisting every raw fact only to enable restart;
- distributed queues before PostgreSQL claims are insufficient;
- caching unbounded graph responses;
- removing validation or checked arithmetic for speed.

## CI Quality Gates

A pull request cannot merge unless:

- restore is locked and succeeds;
- formatting and analyzers are clean;
- the solution builds warning-free;
- all required unit, golden, integration, and frontend tests pass;
- architecture dependency rules pass;
- OpenAPI and generated client are current;
- no unapproved contract breaking change exists;
- schema migrations pass upgrade tests;
- dependency/security scans meet policy;
- changed hot paths meet the selected benchmark regression threshold;
- documentation links and architecture decision references are valid.

Coverage percentage is reported but not used alone as a quality gate. Critical
state transitions, numerical boundaries, recovery paths, and contracts require
explicit scenario coverage.
