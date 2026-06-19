# Operations, Security, and Observability

## Reliability Model

The system assumes processes, networks, and database connections fail. It does
not assume immutable input files change silently, nor that a failed stage can be
continued safely from an arbitrary record.

### Failure containment

- API failure does not stop an already claimed worker attempt.
- Worker failure expires a lease; another worker replays the family.
- One family failure does not corrupt or expose another family's attempt.
- Database unavailability applies bounded backpressure and eventually fails the
  attempt without losing durable run intent.
- Frontend/SSE failure does not affect analysis.
- A malformed input cannot request arbitrary server file access or allocate
  unbounded memory.

### Retry rules

Retry only operations classified as transient and idempotent. Use exponential
backoff with jitter and a maximum elapsed time. Record every retry and final
classification. A deterministic schema, validation, arithmetic, or corruption
failure is not repaired by retrying.

### Timeouts

Define operation-specific timeouts:

- API request and database query;
- PostgreSQL connection acquisition;
- worker lease and renewal;
- bulk write transaction;
- graceful shutdown;
- SSE heartbeat and idle lifetime;
- input header validation.

Do not apply one global timeout to both multi-minute jobs and interactive HTTP.

## Health Endpoints

Expose separate signals following ASP.NET Core health-check guidance:

| Probe | Meaning | Dependencies |
|---|---|---|
| Liveness | Process event loop is functioning | No database or external storage |
| Readiness: API | Can accept authorized queries/commands | Configuration valid, schema compatible, database reachable |
| Readiness: worker | Can claim and persist work | Configuration valid, input roots accessible, schema compatible, database reachable |
| Startup | Initialization/migration compatibility completed | Role-specific |

A currently failed analysis run does not make the API or worker process
unhealthy. Health is about the service's ability to do new work.

## Graceful Shutdown

### API

- stop accepting new connections;
- complete or close SSE streams with reconnect guidance;
- let short in-flight queries finish within the platform grace period;
- dispose data sources and telemetry after requests drain.

### Worker

- stop claiming new families;
- persist shutdown intent/progress;
- cancel local queue producers;
- drain or abort at the configured safe boundary;
- release the attempt if possible, otherwise let the lease expire;
- flush telemetry after state is durable.

Test forced termination as well as graceful shutdown. Correctness cannot depend
on `Dispose` or `finally` executing after a process kill.

## Observability Standard

Use OpenTelemetry-compatible traces, metrics, and structured logs. The API and
worker share correlation conventions but have separate service names and
resource attributes.

### Correlation

Include these identifiers in log scopes and trace attributes where applicable:

- trace/request ID;
- run ID;
- family ID;
- attempt ID;
- worker ID;
- segment ordinal;
- window start;
- analysis-profile version.

Do not use run, family, path, predicate, node, or event IDs as metric labels;
their cardinality is unbounded. Put them in traces/logs.

### Traces

Trace coarse operations:

- create/validate run;
- claim/renew/promote attempt;
- read segment;
- process datum batch;
- close semantic window;
- disparity-filter slice;
- bulk-persist slice;
- serve graph query;
- read/deliver outbox events.

Do not create a span per record or fact. Sample routine successful runs and
retain errors/slow operations at a higher rate.

### Metrics

#### Worker throughput and correctness

- source bytes and records read;
- normalized, unsupported, and malformed datums;
- edge and attribute facts;
- late/missing-timestamp facts;
- documents/interactions created and closed;
- candidate and retained edges;
- open-window count and watermark lag;
- family/slice duration;
- deterministic hash mismatches.

#### Backpressure and resources

- queue depth/capacity and producer wait time;
- active family lanes;
- process CPU and memory;
- GC heap, allocation rate, pause duration, and large-object heap;
- file read throughput;
- database pool usage, copy rows/bytes/duration, WAL and transaction latency.

#### API and UI

- request count, latency, size, status, and canceled queries by route template;
- rate-limit rejections;
- SSE connections, reconnects, event lag, and slow-client closures;
- graph query candidate/returned counts and truncation;
- frontend navigation, fetch, graph layout/render duration, and client errors.

### Logs

Logs are structured and event-named. Record lifecycle transitions, manifest
validation summaries, attempt claims, retries, terminal errors, and promotion.
Do not log every record. Rate-limit repeated malformed-input diagnostics and
store only bounded samples.

Never log:

- credentials, connection strings, tokens, or cookies;
- unredacted arbitrary source properties;
- full physical filesystem paths in user-facing logs;
- entire evidence payloads;
- raw exceptions returned to clients.

## Service-Level Objectives

Define SLOs after baseline measurement. Initial candidates:

| Indicator | Candidate objective |
|---|---|
| API availability | 99.9% successful eligible requests per month |
| API query latency | P95 under 500 ms, P99 under 2 s for bounded reference queries |
| Command durability | 99.9% accepted run commands visible durably within 2 s |
| Cancellation observation | P95 under 5 s while worker is healthy |
| Worker recovery | Expired attempt reclaimed within two lease intervals |
| Event freshness | P95 promoted event visible to connected clients within 2 s |
| Determinism | Zero hash mismatches for identical manifests/profiles |

Do not state processing completion SLOs without dataset-size classes. Define
throughput or completion targets by manifest bytes/records.

## Alerting

Page or create actionable alerts for:

- API readiness failure across replicas;
- worker lease renewal failures or no claims despite queued work;
- repeated family replay beyond policy;
- database pool exhaustion, disk/WAL pressure, or backup failure;
- sustained queue saturation with falling throughput;
- memory nearing the worker budget;
- deterministic replay hash mismatch;
- outbox delivery lag beyond retention safety;
- migration/schema incompatibility;
- high authentication or rate-limit anomaly.

Warnings without an owner, threshold rationale, and runbook should not be alerts.

## Security Threat Boundaries

The primary assets are source evidence, analysis integrity, investigation data,
run control, and database availability.

Primary threats include:

- arbitrary filesystem read through user-supplied paths;
- path traversal or symlink escape from an allowed root;
- malicious/corrupt Avro causing excessive allocation or CPU;
- unauthorized creation/cancellation of expensive runs;
- cross-case or cross-tenant data access;
- leakage of physical paths and evidence metadata;
- SQL injection or unsafe dynamic filtering;
- denial of service through broad graph queries or SSE connections;
- secrets committed to source or exposed in logs;
- stale worker writing after lease loss;
- tampering with input files after manifest creation;
- dependency or generated-schema supply-chain compromise.

## Security Controls

### Identity and authorization

- Authenticate through a trusted OIDC provider.
- Authorize with explicit reader/operator/admin policies.
- Apply resource-level access if runs belong to cases or tenants.
- Default deny; test every endpoint's anonymous and wrong-role behavior.
- Require anti-forgery protection for cookie-authenticated browser commands.

### Input-source control

- Administrators register logical source IDs and allowlisted roots.
- API callers select a source ID and permitted relative dataset, not an absolute
  path.
- Canonicalize after joining to the root; reject traversal and symlink/reparse
  escapes.
- Require approved extensions and family naming.
- Enforce manifest file-count, total-size, record/property-size, and runtime
  quotas.
- Fingerprint sources and reject changes during a run.
- Run workers under an OS identity with read-only access to source roots.

### Data and database

- Use parameterized SQL exclusively.
- Separate schema-owner, API-read/write, and worker-write database roles.
- Restrict network access and require encrypted database connections in
  production.
- Encrypt disks/backups and apply retention by case policy.
- Replace physical source paths in public evidence with opaque source/segment
  IDs. Resolve physical locations only for authorized operators.
- Audit evidence access when required by forensic policy.

### API protection

- Use same-origin BFF or strict configured CORS, never wildcard credentialed
  origins.
- Rate-limit command endpoints by actor and expensive queries by actor/run.
- Set request body/header limits and response limits.
- Apply security headers and TLS at the edge.
- Return Problem Details without internal details.
- Bound SSE connections per actor and enforce idle/maximum lifetimes.

### Secrets and configuration

- Keep production secrets in the platform secret store or environment injection,
  not committed JSON.
- Commit safe defaults only.
- Validate required configuration and ranges at startup.
- Redact secrets in configuration dumps and telemetry.
- Rotate database and OIDC credentials without rebuilding application images.

### Supply chain

- Pin .NET SDK and Node versions.
- Use central package versions and reproducible lock files.
- Automate dependency vulnerability and license scanning.
- Generate an SBOM for release artifacts.
- Sign release artifacts/images where the platform supports it.
- Rebuild generated Avro classes deterministically and verify a clean diff.

## Configuration Model

Group and validate immutable options:

- input-source catalog and quotas;
- worker lanes, batch/queue capacity, lease and shutdown durations;
- Avro schema allowlist and diagnostic policy;
- semantic window/lateness/late-data profile;
- disparity alpha and evidence cap;
- PostgreSQL timeouts, pools, and bulk chunk size;
- API pagination, graph, SSE, and rate limits;
- retention and cleanup;
- telemetry sampling and exporters.

Every run stores the canonical effective analysis profile. Infrastructure tuning
may change between attempts only when it cannot change results; record relevant
tuning in attempt metadata for performance analysis.

## Deployment and Release

1. Build immutable API, worker, and web artifacts from one commit.
2. Run unit, architecture, integration, contract, security, and benchmark gates.
3. Generate and compare OpenAPI and TypeScript client artifacts.
4. Generate an SBOM and scan dependencies/images.
5. Back up and run reviewed schema migration with the migration identity.
6. Deploy API replicas and verify readiness.
7. Deploy workers with claims initially disabled if compatibility requires it.
8. Enable claims and watch leases, throughput, DB pressure, and error budget.
9. Deploy the web application after compatible API availability.
10. Retain rollback artifacts; use expand/contract schema changes so rollback is
    operationally possible.

## Required Runbooks

- API cannot reach PostgreSQL;
- worker repeatedly loses lease;
- family fails validation or Avro parsing;
- deterministic hash mismatch;
- cancellation does not complete;
- database disk or connection pool pressure;
- outbox/SSE lag;
- failed migration or incompatible binary;
- source changed after manifest creation;
- restore from backup and verify hashes;
- safely prune failed attempts and detailed results.
