# Pipeline Design

## Design Goals

The processing pipeline must preserve current analytical meaning while making
work durable, restartable, observable, and safe to scale. It processes immutable
families in bounded memory and writes only attempt-scoped results until a family
completes successfully.

## Unit of Work

The default durable unit is one input family, not one file, record, or window.

A family is the correct replay boundary because the current semantic state is
family-scoped and includes:

- entity metadata observed so far;
- maximum event timestamp and closed-through watermark;
- open event-time windows;
- rolling document frequency;
- accumulated node-kind baselines;
- each node's previous-window term set;
- late-event distribution;
- cumulative stage metrics.

Resuming at an arbitrary Avro block without restoring all of this state would
produce incorrect results. Persisting the complete state frequently would be
expensive and tightly coupled to algorithm internals. On failure, discard the
failed family attempt and replay that family from its immutable manifest.

For exceptionally large single families, add versioned state snapshots only
after measurements show replay time is unacceptable. A snapshot format then
becomes a compatibility contract and requires migration tests.

## Run Lifecycle

```text
Requested
  -> Validating
  -> Queued
  -> Running
       -> Cancelling -> Canceled
       -> Failed
       -> Completed
```

Rules:

1. Creating a run stores the requested input source, idempotency key, creator,
   and analysis profile in one transaction.
2. Validation creates an immutable manifest before any family attempt runs.
3. A run is `Running` while at least one family is active or runnable.
4. Cancellation is a persisted request, not only a process token.
5. Terminal states include a machine-readable reason and partial-result policy.
6. `Completed` means every manifest family has one promoted successful attempt.
7. Failed or abandoned attempts never become visible through investigation
   queries.

## Manifest Phase

The manifest is a reproducibility artifact, not a transient list of paths.

For every input source record:

- resolve an administrator-configured source identifier to an allowlisted root;
- canonicalize the path and reject traversal or a symlink escape;
- enumerate only supported family patterns;
- require a base and contiguous positive segment suffixes;
- assign stable family and segment ordinals before processing;
- capture logical source name, relative path, size, last-write time, and a
  fingerprint policy result;
- read and validate each Avro container header and writer schema;
- record total bytes and expected segment count;
- reject duplicates using platform-correct path identity and manifest identity;
- persist the manifest and validation diagnostics transactionally.

Fingerprint policy should be configurable:

| Policy | Cost | Use |
|---|---:|---|
| Metadata only | Low | Trusted immutable local archive |
| Header plus sampled blocks | Medium | Default defense against accidental replacement |
| Full SHA-256 | High | Compliance or untrusted transfer |

Before every attempt, recheck size and metadata. If the source changed, fail the
run rather than silently analyzing a different dataset under the same run ID.

## Processing Stages

```text
Manifest family
  -> segment reader
  -> CDM normalization
  -> fact projection
  -> semantic window accumulator
  -> closed semantic slices
  -> disparity reduction
  -> attempt-scoped bulk writer
  -> family attempt promotion
```

### Segment reader

- Open one segment at a time with sequential-scan hints and measured buffer size.
- Validate the embedded writer schema before consuming records.
- Emit the previous Avro sync-block offset as block-level provenance.
- Classify header, schema, truncation, I/O, and datum errors separately.
- Check cancellation between blocks and bounded batches, not with expensive
  synchronization per scalar operation.
- Never retain generated Avro records after normalization.

Apache Avro's current C# API is synchronous. Run the reader in a dedicated
worker lane; do not wrap each record in `Task.Run` or pretend synchronous reads
are asynchronous.

### Normalization

- Replace per-record reflection with explicit or generated type mappers for
  supported CDM18 records.
- Centralize predicate naming, scalar formatting, UUID conversion, and unknown
  value policy.
- Treat generated records as untrusted external input.
- Bound string/property sizes and collection counts before allocating or
  persisting them.
- Emit a typed result: supported datum, unsupported datum, malformed datum, or
  fatal container error.
- Record a bounded sample of diagnostic reasons; metrics alone are insufficient
  for troubleshooting, but unbounded error retention is unsafe.

### Fact projection

- Keep projection pure apart from an injected metrics recorder.
- Preserve deterministic emission order for the first and second predicate
  objects.
- Make missing subject/object policy part of the analysis profile.
- Use a compact internal representation for hot-path facts. Do not serialize,
  box, or format GUIDs in this stage.

### Semantic indexing

Analysis profile v1 preserves current semantics:

- fixed, Unix-epoch-aligned tumbling windows;
- family-local watermark and corpus state;
- maximum-seen event time minus allowed lateness;
- no reopening an emitted window;
- observed-order metadata application;
- rolling family-local TF-IDF and term-set Jaccard;
- bounded evidence per predicate interaction;
- deterministic chronological window emission.

Make each semantic rule explicit and versioned. In particular, document that
metadata with no event timestamp affects subsequent facts only. If a later
profile performs a metadata prepass or buffers unresolved edges, it is a new
profile because results change.

Late-data policy must be one of:

- `DropAndCount` for compatibility;
- `FailFamily` for strict forensic workflows;
- `Quarantine` to retain bounded diagnostic evidence outside the primary result.

The selected policy is immutable for the run.

### Disparity reduction

- Validate that all interaction endpoints exist in the slice.
- Consolidate predicate interactions by directed node pair.
- Sum counts and weights with checked integer and finite-floating-point guards.
- Build source-outgoing and target-incoming populations.
- Preserve the degree-one policy explicitly.
- Apply strict `< alpha` significance and retain on either direction for profile
  v1.
- Sort retained documents, edges, predicate maps, terms, and evidence at the
  output boundary only.
- Compute a deterministic content hash over the canonical slice result.

### Attempt writer

- Write every row with run, family, and attempt identity.
- Bulk-copy documents and interactions in bounded chunks.
- Persist the summary, result hash, metrics, and completion marker.
- Never update the active family pointer until all attempt output is durable.
- Promote the attempt with a compare-and-swap guarded by its fencing token.
- Publish outbox events in the same promotion transaction.

## Batching and Backpressure

The safest first implementation is one ordered processing lane per family with
bounded batches between I/O and CPU stages.

Suggested initial model:

```text
reader/normalizer
  -> bounded queue of datum batches
  -> projector/indexer (ordered, one consumer per family)
  -> bounded queue of closed slices
  -> disparity workers
  -> bounded queue of result batches
  -> PostgreSQL writer
```

Principles:

1. Queue capacity is configured in bytes or estimated batch weight, not only
   item count.
2. Full queues wait; they do not drop analysis data.
3. Queue depth, wait time, batch size, and throughput are metrics.
4. Batch size is benchmarked. Start in the hundreds or low thousands of facts,
   not one task per fact.
5. Pooled buffers are introduced only if allocation profiles justify them and
   ownership remains unambiguous.
6. The semantic accumulator stays single-threaded within a family because
   watermark, document frequency, and previous-self state are ordered.
7. Closed slices may be disparity-filtered concurrently because each slice is
   independent, but persisted events are resequenced by window ordinal if UI or
   hashing expects chronological delivery.

An even simpler fully synchronous family lane is acceptable for the first
equivalence milestone. Bounded channels should be introduced only with a
measured throughput reason and explicit shutdown tests.

## Parallelism Strategy

### Across families

Families are independent analysis corpora in profile v1, so bounded family
parallelism is the safest scale-out axis. Stable family IDs come from the
manifest, not completion order.

Configure concurrency from measured resources:

- storage read bandwidth;
- CPU cores;
- worker memory budget per lane;
- PostgreSQL write throughput and connection budget.

Default to one lane. Increase only when full-dataset benchmarks show a benefit
without violating API latency or memory budgets.

### Within a family

Keep reader order and semantic accumulation serial. Potential parallel work is
limited to:

- static normalization of a batch, if record ordering is restored;
- pure disparity filtering of already-closed slices;
- serialization/bulk preparation after canonical results exist.

Do not partition a family by segment unless cross-segment event-time, metadata,
and rolling corpus semantics are redesigned.

## Lease and Fencing Protocol

Each family attempt has:

- attempt ID;
- worker ID;
- lease expiry;
- monotonically increasing fencing token;
- heartbeat time;
- attempt state;
- progress counters and last safe source location.

Claiming is transactional. Renew only when the current worker and fencing token
match. Every result write includes the token. Promotion fails if a newer worker
has reclaimed the family. This prevents a slow, partitioned worker from
overwriting a newer attempt after its lease expired.

Lease duration must exceed normal database pauses and be renewed well before
expiry. A lease is not a cancellation mechanism; cancellation is a separate
durable flag observed by the worker.

## Idempotency and Visibility

Exactly-once execution is not promised. At-least-once family execution plus
idempotent attempt visibility is sufficient:

- retries create a new attempt;
- output is keyed by attempt ID;
- readers join through `families.active_attempt_id`;
- only successful promotion changes that pointer;
- duplicate command submission returns the run associated with its idempotency
  key;
- a promoted content hash detects nondeterministic replay;
- failed attempt output is deleted asynchronously after a retention period.

## Cancellation and Shutdown

1. The API persists `cancel_requested_at` and emits an outbox event.
2. Workers poll cancellation with lease renewal and also receive a local signal.
3. Readers stop at a safe batch/block boundary.
4. Queues complete from producer to consumer; consumers drain only work already
   accepted when policy allows.
5. The attempt records `Canceled`, remains unpromoted, and releases its lease.
6. Host shutdown first stops new claims, then allows a configured grace period,
   then abandons the lease for safe retry.

Do not mark a run completed from a disposal callback. Completion is an explicit
application transition after all writes and promotion succeed.

## Error and Retry Policy

| Failure | Retry? | Result |
|---|---|---|
| Invalid manifest or unsupported schema | No | Run validation failed |
| Deterministic malformed datum | Profile policy | Count/drop, quarantine, or fail family |
| Truncated/corrupt Avro container | No automatic retry unless source may still be uploading | Family failed |
| Transient PostgreSQL/network error | Yes, bounded exponential backoff with jitter | Same attempt transaction retried or attempt replayed |
| Serialization/invariant failure | No | Family failed; alert |
| Lease lost | No write/promotion | Attempt abandoned; new worker replays |
| Disk full/database capacity | Limited retry, then fail safely | No promotion |
| Operator cancellation | No | Canceled |

Retries occur at idempotent transaction or family boundaries. Never continue
after an unknown partial write without using attempt identity to isolate it.

## Progress and Metrics

Persist coarse progress periodically, not per record:

- current family and segment ordinal;
- bytes read and total manifest bytes;
- records, facts, windows, candidates, and retained edges;
- last Avro sync-block position for diagnostics;
- current watermark and open-window count;
- queue depths and backpressure time;
- attempt heartbeat and estimated throughput.

Progress is advisory and may move backward when an attempt replays. Completion
is based only on promoted attempts.

## Determinism Contract

A run records:

- input manifest hash;
- CDM schema identity;
- analysis-profile name and canonical configuration hash;
- application build/version;
- normalization-map version;
- database schema version.

For each promoted family, store canonical hashes for summaries and optionally
each slice. CI and replay tools compare these hashes across worker concurrency,
operating systems where supported, and implementation changes.
