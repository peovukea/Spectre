# Data and Storage Design

## Storage Responsibilities

PostgreSQL serves four related but distinct responsibilities:

1. durable run and cancellation state;
2. family job claims, leases, and attempt history;
3. promoted investigation read models;
4. a durable event outbox for API/SSE delivery.

Keep these concerns in separate repository classes and schemas even when they
share one database. A single all-purpose store class should not return HTTP DTOs
and manage worker leases.

## Schema Areas

Use a dedicated schema such as `spectre` and group tables by purpose.

### Run ledger

#### `runs`

Recommended fields:

- run ID;
- input-source ID;
- idempotency key and requester identity;
- state and reason code;
- requested, validated, started, cancel-requested, and completed timestamps;
- immutable analysis-profile JSON and hash;
- manifest hash;
- application and schema versions;
- partial-result policy;
- aggregate progress and terminal metrics;
- optimistic concurrency/version column.

The row is the authority for lifecycle state. Elapsed time is derived from
timestamps rather than an in-memory stopwatch.

#### `run_manifests`, `manifest_families`, `manifest_segments`

Store immutable logical names, relative source paths, ordinals, sizes,
fingerprints, schema identity, and validation results. Physical root paths are
in server configuration, not exposed through public APIs.

### Work and attempts

#### `family_jobs`

One row per run/family with desired state, active promoted attempt, retry count,
priority, next-attempt time, and terminal outcome.

#### `family_attempts`

One row per execution attempt with worker, fencing token, lease expiry,
heartbeat, state, progress, timestamps, error classification, bounded diagnostic
summary, metrics, and result hash.

Claims use row locking or an atomic update equivalent to `FOR UPDATE SKIP
LOCKED`. The exact SQL belongs in the PostgreSQL adapter and must be tested with
concurrent workers.

### Analysis read model

#### `families`

Stable manifest-assigned identity, logical key/name, active attempt ID, and
window range. Physical absolute paths are not stored as user-facing identity.

#### `slice_summaries`

Attempt ID, family ID, window key, counts, aggregate weights, Jaccard summaries,
reduction metrics, content hash, and retention level.

#### `node_documents`

Attempt/family/window/node key, node kind, display label fields, Jaccard values,
term counts, and TF-IDF weights.

#### `backbone_interactions`

Attempt/family/window/source/target key, scalar counts and weights, directional
scores, bounded evidence, and compact term/predicate payloads.

#### Filter dimensions

Do not discover every predicate and node kind by scanning JSON objects for every
request. Maintain attempt-scoped dimension or summary tables for:

- observed predicates;
- observed node kinds;
- optional predicate counts per slice.

Use JSONB for bounded, schemaless detail returned as a unit. Use relational
columns/tables for values used in joins, filters, ordering, uniqueness, or
foreign keys.

### Event outbox

#### `run_events`

Monotonic event ID, run ID, family/window identity when applicable, event type,
schema version, payload, creation time, and optional expiry. Insert events in the
same transaction that changes visible state.

SSE readers resume after `Last-Event-ID`. PostgreSQL `LISTEN/NOTIFY` may wake API
instances, but the table is the source of truth because notifications can be
lost.

## Attempt Isolation and Promotion

Directly replacing rows in the currently visible family makes partial retries
observable. Use generation/attempt isolation instead:

1. create an attempt and acquire a fencing token;
2. write summaries, documents, and interactions under that attempt ID;
3. verify counts, hashes, and lease ownership;
4. in one short transaction, mark the attempt successful, update
   `families.active_attempt_id`, update job state, and append outbox events;
5. queries resolve data only through the active attempt;
6. delete old failed/unreferenced attempts asynchronously.

This gives atomic family visibility without holding a transaction open for the
entire family or deleting good data before replay succeeds.

## Transaction Boundaries

| Operation | Transaction boundary |
|---|---|
| Create run and idempotency record | One short transaction |
| Persist validated manifest and family jobs | One transaction, or bounded chunks plus final validation marker |
| Claim/renew attempt | One short compare-and-swap transaction |
| Persist one closed slice | One transaction containing summary and bulk detail writes |
| Complete/promote family attempt | One short fenced transaction with outbox |
| Set run terminal state | One short transition transaction with outbox |
| Apply retention | One attempt/family/window-scoped transaction |

Do not hold a database transaction while reading Avro or calculating a window.

## Bulk-Write Strategy

- Keep Npgsql binary `COPY` for large document and interaction sets.
- Copy into attempt-scoped staging or final attempt tables.
- Use bounded chunks so one pathological slice cannot require an unbounded
  client-side buffer.
- Prepare JSON once per row at the persistence boundary.
- Reuse prepared commands for scalar summary writes where Npgsql benefits.
- Track copy rows, bytes, duration, retries, and WAL pressure.
- Set connection and command timeouts by operation class.
- Reserve separate connection-pool budgets for API reads and worker writes.

The current per-slice delete-before-copy approach becomes unnecessary when a new
attempt ID provides isolation.

## Query Model

### General rules

- All list endpoints are paginated, including runs, families, and windows.
- Use keyset/cursor pagination for stable large result sets.
- Apply `LIMIT` in SQL. Do not read every match merely to discard results in the
  application.
- Return an exact total only when explicitly requested; otherwise return
  `hasMore` or a bounded estimate.
- Query promoted attempts only.
- Make sort order explicit and stable.
- Set statement timeouts for interactive requests.
- Cancel database commands when HTTP requests are aborted.

### Graph projection

The projection query should use a two-step bounded plan:

1. select top candidate interactions using run/family/window, filters, stable
   order, and a database-side edge limit;
2. fetch the distinct endpoint documents for accepted edges.

Node caps complicate a single SQL limit because an edge can add zero, one, or
two nodes. Resolve this with a bounded over-fetch factor and application
selection, not an unbounded scan. Report truncation and continuation cursor.

### Expensive filters

- Index semantic weight in the query order.
- Normalize predicates used for filtering into an indexed relation if JSONB key
  tests become a bottleneck.
- Index node kind by attempt/family/window/kind.
- Test plans with production-like cardinality using `EXPLAIN (ANALYZE, BUFFERS)`.
- Do not add GIN indexes speculatively; measure write amplification and query
  benefit.

## Indexing Baseline

Initial indexes should support demonstrated access patterns:

- runs by requested/started time and state;
- runnable family jobs by desired state, priority, and next-attempt time;
- lease expiry for abandoned-work recovery;
- family identity by run and manifest ordinal;
- slice summaries by active attempt, family, and window;
- node documents by attempt/family/window/node and by node kind;
- interactions by attempt/family/window/weight/source/target;
- outbox events by run/event ID and expiry.

Every index must have an owner query and a measured plan. Remove redundant
indexes because large COPY workloads pay for each one.

## Partitioning and Retention

Do not partition immediately. The reference dataset and run count should first
demonstrate table/index pressure. When needed, partition result tables by run ID
hash or run-time range only if it improves retention and query plans.

Retention policy is explicit per data class:

- run metadata and summaries: long-lived;
- promoted detailed attempts: configurable by age, tag, or storage quota;
- failed attempt output: short diagnostic retention;
- outbox events: retain long enough for client replay, then prune;
- source manifests and hashes: retained with the run;
- raw CDM files: external lifecycle, never deleted by Spectre.

When detail is pruned, update retention state transactionally before or with
deletion so queries return a deliberate `410 Gone` equivalent rather than an
ambiguous `404`.

## Migrations

- Generate reviewed, forward-only migrations in source control.
- Run production migrations as an explicit deployment step with a dedicated
  schema-owner credential.
- API and worker credentials do not own schema changes.
- Each binary declares the supported schema-version range and refuses unsafe
  startup.
- Test upgrade from the last supported production schema with representative
  data.
- Use expand/migrate/contract changes for zero-downtime deployments.
- Back up and restore-test before destructive changes.

Automatic `Database.Migrate()` at every API startup is acceptable only for
local development.

## Time and Numeric Representation

- Keep source event time as signed 64-bit Unix nanoseconds for lossless CDM
  compatibility.
- Keep UTC lifecycle timestamps as PostgreSQL `timestamptz`.
- Convert nanoseconds to display timestamps only at a boundary and preserve the
  original value.
- Serialize 64-bit integers as decimal strings in JSON until the frontend uses a
  transport with native 64-bit integer support.
- Reject overflow and non-finite weights at the domain boundary.
- Record algorithm math/version so floating-point changes are auditable.

## Backup and Recovery

- Enable automated PostgreSQL backups and point-in-time recovery appropriate to
  the deployment.
- Restore into a clean environment on a schedule and verify run/result hashes.
- Document recovery-point and recovery-time objectives.
- Treat source manifests as pointers to external immutable data; database backup
  alone does not preserve replay capability if source files disappear.
- Export run configuration, manifest, and result hashes as a compact provenance
  bundle for long-term reproducibility.
