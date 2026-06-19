# Current-State Assessment

## Scope and Evidence

This assessment is based on the current repository, not only its READMEs. The
review covered all project references, public contracts, processing stages,
PostgreSQL schema and queries, HTTP endpoints, Next.js data access, and tests.

Verification performed during this assessment:

- all 71 .NET tests pass;
- frontend ESLint passes;
- the Next.js 16.2.9 production build and TypeScript check pass;
- the .NET build still emits XML-documentation warnings in hand-authored public
  types, so it is not warning-clean.

## Existing Runtime Shape

```text
ASP.NET Core host process
  |
  +-- minimal API endpoints
  +-- in-memory run controller and Task
  +-- synchronous ingestion runner
  |     +-- filesystem family discovery
  |     +-- Apache Avro reader and normalization
  |     +-- graph-fact projection
  |     +-- semantic indexing sink
  |     +-- disparity filtering sink
  |     `-- PostgreSQL slice sink
  +-- PostgreSQL command/query store
  `-- in-memory SSE fan-out

Next.js process
  `-- one large client dashboard using a rewrite to the API
```

The stages form an ownership chain of synchronous `IDisposable` sinks. Disposing
the ingestion sink flushes semantic windows, disposes the filter, closes the
store adapter, and finalizes metrics.

## Existing Data Flow

1. A remote request starts a run with an optional physical input path.
2. The web host starts an untracked `Task.Run` and holds its cancellation token
   in memory.
3. Discovery validates all `.bin` families and contiguous segments.
4. `AvroReader` streams one specific record at a time with a 64 KiB sequential
   file buffer and records the previous Avro sync-block offset.
5. Reflection-based normalization maps selected CDM18 entities and events into
   normalized records.
6. Projection emits edge or attribute facts and counts unsupported/malformed
   data.
7. Semantic indexing maintains family-scoped metadata, active tumbling windows,
   rolling TF-IDF state, Jaccard baselines, and bounded evidence.
8. A maximum-seen-timestamp watermark closes windows; data arriving after a
   window closes is counted and dropped.
9. Disparity filtering consolidates predicate interactions by directed node
   pair, computes source-outgoing and target-incoming significance, and retains
   an edge when either direction passes the configured alpha.
10. Each closed backbone slice is synchronously persisted to PostgreSQL in one
    transaction using scalar statements and binary `COPY` for detail rows.
11. The API reads the same tables and broadcasts non-durable in-memory SSE
    notifications.

## What Is Already Good

### Streaming and bounded stage state

The reader and projection stages are lazy, raw records are not retained, open
semantic windows are evicted, evidence is capped, and disparity state is scoped
to one closed slice. This is the right foundation for large input files.

### Preflight validation

Input families are discovered and validated before output sinks are created.
Missing bases, direct segment inputs, duplicate families, and gaps fail before
processing. Deterministic family and segment order is explicit.

### Source evidence

Facts retain physical segment and Avro sync-block provenance. The current code
correctly avoids claiming that this is an exact record byte offset.

### Explicit event-time concepts

Window duration, allowed lateness, watermarks, late-event metrics, family
boundaries, and flushing behavior are visible concepts rather than accidental
side effects.

### Deterministic output ordering

Documents, interactions, predicates, attributes, and evidence use stable sorts
at emission boundaries. This makes golden comparison feasible.

### Bounded query responses

Graph responses cap nodes and edges, validate filters, distinguish not-found
from removed detail, and serialize 64-bit values as strings to avoid JavaScript
precision loss.

### PostgreSQL bulk insertion

Binary `COPY` is appropriate for per-slice document and interaction inserts.
The existing composite keys encode important uniqueness rules.

### Existing behavioral coverage

The tests cover input discovery, Avro errors and normalization, projection,
sink ownership, family isolation, watermarks, TF-IDF/Jaccard behavior,
disparity significance, evidence, and important query-store behavior.

## Structural Problems

### Contract projects form a pipeline-shaped dependency chain

The five `*.Api` assemblies are not a clean application core. Indexing contracts
depend on ingestion contracts, backbone contracts depend on indexing contracts,
investigation contracts depend on both, and pipeline contracts depend on the
investigation API. This mirrors execution flow rather than stable domain
boundaries and forces downstream consumers to inherit upstream types.

Implementation projects also contain stale duplicate contract files excluded by
`Compile Remove`. That is a strong sign that ownership moved but repository
structure did not finish moving.

### Concrete implementation dependencies cross stage boundaries

The disparity implementation references the semantic-indexing implementation,
and the host casts stage-created abstractions back to concrete sink types to
obtain metrics. The abstraction therefore does not describe everything the host
needs and does not actually isolate implementations.

### Disposal is being used as a business protocol

Window flush, downstream completion, metric finalization, and resource cleanup
all occur through nested disposal. Disposal should release resources; it should
not be the only way to express successful completion. The current chain makes
partial failure and ownership difficult to reason about.

### The web host owns durable work in memory

Run existence is persisted, but scheduling is not. The active task, cancellation
token, timer, and current run identity live in singleton memory. A host restart
marks a run failed rather than resuming it. Multiple API instances would each
believe they can start a run. There is no lease, fencing token, durable cancel
request, or claimed-work protocol.

### Synchronous work runs behind an API singleton

The analysis itself is intentionally synchronous, but its orchestration belongs
in a worker, not an ASP.NET singleton launched via `Task.Run`. API responsiveness,
process shutdown, job lifetime, and analysis resource use are coupled.

### No explicit backpressure boundary

The all-synchronous chain naturally backpressures today, which is safe but makes
concurrency and stage measurement difficult. If any stage becomes asynchronous
without a bounded queue contract, memory can grow without limit. The design
needs an intentional batching and backpressure model before parallelism.

### Failure recovery is run-wide and ambiguous

There is no durable family attempt. A crash can leave detailed rows for some
windows and no explicit indication of which family generation is complete.
Retrying a run creates another run rather than safely replaying a failed family.
`MarkWritesClosed` is empty, so a named lifecycle transition has no state effect.

### Live events are lossy and non-replayable

Each API process owns its own channels. Slow subscribers drop oldest events,
process restarts lose all events, and `Last-Event-ID` is read but ignored. The
frontend compensates by polling every ten seconds, which is correct defensive
behavior but exposes the missing durable event contract.

### One store class owns too many concerns

`PostgresInvestigationStore` owns run state, timing, job creation, recovery,
slice transactions, bulk import, summaries, graph queries, DTO mapping, memory
reporting, and event publication. It combines command and query paths and makes
transaction boundaries hard to test independently.

### HTTP and persistence models are coupled

Database access directly constructs HTTP DTOs, JSON converters live in the
investigation contract assembly, and public DTOs contain algorithm contract
types. A persistence or algorithm refactor can therefore become an HTTP breaking
change.

### Hot-path reflection and small-object traffic

Entity normalization reflects over every supported record instance and sorts
properties repeatedly. The pipeline exchanges individual datums and facts,
creates many short-lived records, dictionaries, arrays, and strings, and formats
GUIDs for ordering. These may be acceptable now, but they are the first measured
optimization candidates because they execute tens of millions of times.

### Query caps do not cap database work

The graph query reads every matching interaction to calculate a total and then
discards edges beyond node/edge caps. A broad query can therefore perform large
database and application work while returning a small response. Pagination and
database-side limits must be explicit.

### Configuration and security are development-oriented

- A database password is committed in application configuration and Compose.
- CORS is hard-coded for a local origin.
- run start accepts an arbitrary server filesystem path;
- there is no authentication, authorization, or audit trail;
- physical segment paths are exposed as evidence;
- migrations run automatically at API startup;
- algorithm options are only partly configuration-bound and are not validated
  as a complete immutable analysis profile.

### The frontend is one large client boundary

The investigation page renders a single large Client Component containing most
fetching, reconciliation, selection, filters, control, and presentation state.
It works, but gives up Server Component initial rendering, route-level loading
and error boundaries, URL-addressable investigation state, and generated API
contracts.

### Documentation and build policy have drifted

Some READMEs reference old class names and incorrect solution paths. Public XML
documentation generation is enabled without a warning-clean policy. Framework
and package versions are repeated across project files, and there is no visible
central package management, repository-wide editor configuration, analyzer
policy, or architecture-test gate.

## Semantic Decisions That Must Be Frozen Before Rebuild

The current behavior is not merely plumbing. These rules affect results and
must be captured by golden tests before replacement:

1. Families are independent analysis corpora and reset metadata, watermark,
   TF-IDF document frequency, node-kind baselines, and previous-self baselines.
2. Windows are fixed, epoch-aligned, and keyed in Unix nanoseconds.
3. Metadata affects only facts processed after the metadata record.
4. Edges without timestamps are skipped.
5. Late facts targeting emitted windows are skipped, not reopened.
6. TF uses `1 + log(count)` and IDF uses the current rolling family corpus.
7. Jaccard compares term sets, not weighted vectors.
8. Predicate interactions consolidate into one directed node-pair candidate.
9. A degree-one direction is not automatically significant.
10. Retention occurs when source-outgoing or target-incoming significance is
    strictly less than alpha.
11. Evidence ordering and truncation are deterministic.
12. Unknown subject subtypes fall back to process and are counted.

Any desired semantic change should be a new named analysis-profile version,
not an incidental consequence of architectural work.

## Retain, Replace, Reconsider

| Area | Decision |
|---|---|
| Avro specific-record compatibility | Retain |
| Global preflight manifest validation | Retain and strengthen |
| Family-scoped analysis | Retain as profile v1 |
| Bounded evidence and query sizes | Retain |
| PostgreSQL and binary COPY | Retain |
| Deterministic ordering | Retain and hash |
| Synchronous sink ownership chain | Replace |
| In-memory web-host task | Replace |
| In-memory SSE hub | Replace |
| Duplicate API/implementation contracts | Replace |
| Reflection normalization | Reconsider after baseline profiling; prefer generated/static mappers |
| Raw-fact persistence | Do not add by default |
| Automatic production startup migrations | Replace with explicit deployment migration |
| Arbitrary input paths over HTTP | Remove |
| One large client dashboard | Split into route/server shell and interactive islands |
