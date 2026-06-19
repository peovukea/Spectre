# Architecture Decisions

This file is the initial ADR register. Each accepted decision should eventually
move into its own immutable ADR with context, decision, consequences, date, and
superseding links.

## ADR-001: Modular Monolith, Separate API and Worker Roles

**Status:** Proposed for acceptance

**Decision:** Keep one bounded context and shared application core, but deploy an
ASP.NET Core API process separately from a .NET worker process.

**Why:** Long-running analysis has different scaling, shutdown, and failure
needs from interactive queries. A full microservice split would add network
contracts, deployment units, and distributed failure without current evidence.

**Consequences:**

- API remains responsive and restartable during jobs.
- Worker scaling is independent.
- One database remains a shared operational boundary.
- Application modules must enforce dependency rules internally.

**Rejected:** One all-in-one web process; microservice per pipeline stage.

## ADR-002: PostgreSQL Durable Queue Before a Message Broker

**Status:** Proposed for acceptance

**Decision:** Use PostgreSQL jobs, leases, fencing tokens, and an outbox for the
initial durable scheduler and event stream.

**Why:** PostgreSQL is already required, work volume is coarse family jobs, and
transactional state changes matter more than very high message throughput.

**Consequences:**

- Fewer moving parts and no dual-write to a broker.
- Claim SQL and indexes become critical infrastructure.
- Database capacity must isolate API reads and worker writes.
- Broker extraction remains possible behind application ports.

**Trigger to revisit:** PostgreSQL claim/outbox load materially harms analysis or
query SLOs after tuning, or cross-region/event fan-out requirements emerge.

## ADR-003: Family Is the Default Replay Boundary

**Status:** Proposed for acceptance

**Decision:** Replay a failed family from the beginning rather than resuming an
arbitrary Avro record.

**Why:** Semantic state is family-scoped and cannot be reconstructed correctly
from a source offset alone.

**Consequences:**

- Recovery is simple and correct.
- Failed work may repeat several minutes of processing.
- Inputs must remain immutable and fingerprinted.
- Attempt isolation makes replay invisible until promotion.

**Trigger to revisit:** P95 family replay time exceeds the agreed recovery
budget. Any snapshot design must version the full semantic state.

## ADR-004: Attempt-Scoped Writes and Atomic Promotion

**Status:** Proposed for acceptance

**Decision:** Write results under a family attempt ID, then atomically switch the
family's active attempt after successful completion.

**Why:** A transaction cannot remain open for an entire family, while direct
replacement exposes partial retry data.

**Consequences:**

- Queries see one complete generation.
- Failed attempts consume temporary storage.
- Cleanup and retention are required.
- Fencing can prevent stale promotion.

**Rejected:** Delete visible rows before replay; one transaction per family;
pretending execution is exactly once.

## ADR-005: Do Not Persist Every Raw Fact by Default

**Status:** Proposed for acceptance

**Decision:** The immutable CDM manifest is the replay source. Persist normalized
facts only if a later product requirement justifies a separate fact lake.

**Why:** The reference run emits more than 52 million facts. Default persistence
would add substantial storage, serialization, WAL, migration, and privacy cost.

**Consequences:**

- Family replay rereads Avro.
- Source retention is part of reproducibility.
- Debugging relies on bounded diagnostics and source evidence.

**Trigger to revisit:** Multiple analyses over the same normalized facts,
cross-schema normalization reuse, or source-access constraints make reread more
expensive than durable normalized storage.

## ADR-006: Preserve Profile v1 Semantics Before Improving Algorithms

**Status:** Proposed for acceptance

**Decision:** Freeze current family-scoped watermark, rolling TF-IDF/Jaccard, and
disparity behavior as analysis profile v1. Algorithm changes create a new
profile.

**Why:** Architectural changes must not silently change forensic results.

**Consequences:**

- Golden equivalence is possible.
- Known semantic limitations remain visible in v1.
- Runs store profile and algorithm versions.

**Rejected:** Treating current output differences as harmless refactor noise.

## ADR-007: Ordered Family Lane with Bounded Batches

**Status:** Proposed for acceptance

**Decision:** Preserve ordered semantic accumulation within a family. Use
bounded batches/queues only around stages where measurement justifies them;
scale first across independent families.

**Why:** Watermarks, metadata, rolling corpus state, and previous-self baselines
are order-sensitive. Per-record task parallelism is expensive and risky.

**Consequences:**

- Predictable memory and determinism.
- One family cannot use all cores without safe independent slice work.
- Family parallelism requires memory and database budgeting.

## ADR-008: PostgreSQL Read Model with Relational Filter Fields

**Status:** Proposed for acceptance

**Decision:** Keep final summaries/documents/interactions in PostgreSQL. Store
indexed/filterable identity and scalar values relationally; use JSONB only for
bounded detail returned as a unit.

**Why:** Current queries and COPY already fit PostgreSQL, but repeated JSON key
discovery and filtering can create unnecessary scans and write-heavy GIN
indexes.

**Consequences:**

- Some additional dimension tables and write work.
- Clearer query plans and constraints.
- Schema evolution remains necessary for new indexed fields.

## ADR-009: SSE with Durable Outbox Replay

**Status:** Proposed for acceptance

**Decision:** Keep SSE for one-way live updates, backed by a PostgreSQL outbox
and `Last-Event-ID` replay.

**Why:** The browser does not need bidirectional streaming, while current
in-memory fan-out loses events and does not work across replicas.

**Consequences:**

- Events survive API restart and load balancing.
- Outbox retention and slow-client policy are required.
- Clients still reconcile query state after events.

**Rejected:** In-memory channels as source of truth; WebSockets without a
bidirectional use case.

## ADR-010: HTTP Contracts Generated to TypeScript

**Status:** Proposed for acceptance

**Decision:** Version external DTOs independently, generate OpenAPI, and generate
the TypeScript client/contracts from that artifact.

**Why:** The current C# and TypeScript contracts are manually synchronized and
public DTOs reference algorithm types.

**Consequences:**

- CI must check generated artifacts and breaking changes.
- Domain models remain free to evolve internally.
- Custom conventions such as 64-bit decimal strings require explicit OpenAPI
  representation and contract tests.

## ADR-011: Next.js Server Shell with Interactive Client Islands

**Status:** Proposed for acceptance

**Decision:** Use App Router Server Components for initial authenticated reads
and stable views; use focused Client Components for SSE, controls, graph, and
browser interaction.

**Why:** The current single client dashboard forfeits route-level data/loading
boundaries and shareable investigation URLs.

**Consequences:**

- Clearer server/client data ownership.
- BFF/auth and caching decisions must be explicit.
- Graph renderer remains client-only.

## ADR-012: Explicit Production Migrations

**Status:** Proposed for acceptance

**Decision:** Run reviewed migrations as a deployment step under a schema-owner
identity. API/worker startup checks compatibility but does not mutate production
schema.

**Why:** Automatic startup migration is unsafe with multiple replicas, long
migrations, and least-privilege credentials.

**Consequences:**

- Deployment pipeline is responsible for migration order and rollback planning.
- Local development may retain automatic migration convenience.

## ADR-013: Static/Generated CDM Normalization

**Status:** Proposed, benchmark required

**Decision:** Prefer explicit or generated mappers for supported CDM18 record
types over reflection on every record.

**Why:** Normalization runs tens of millions of times and current reflection,
property access, sorting, and string creation are likely hot allocation paths.

**Consequences:**

- More generated or explicit mapping source to maintain.
- Schema regeneration must verify mapper completeness.
- Benchmark and golden tests are mandatory.

**Fallback:** Retain a bounded reflection fallback for unknown diagnostics only,
outside the supported hot path.

## ADR-014: Authentication Through OIDC and Role Policies

**Status:** Proposed for acceptance

**Decision:** Authenticate through the organization's OIDC provider and
authorize reader, operator, and administrator policies. Prefer a Next.js
same-origin BFF with secure cookies for browser use.

**Why:** Run control is expensive and physical evidence is sensitive. CORS and
network location are not authorization.

**Consequences:**

- Local development needs a documented test identity provider or dev auth mode.
- API functional tests need policy-specific identities.
- Commands and evidence access are audited.

## Open Decisions Requiring Product or Operations Input

These should not block the core design, but must be resolved before production:

1. What are the actual maximum dataset, family, and concurrent-run sizes?
2. Are input sources local disks, network shares, object storage, or all three?
3. How long must raw input, detailed results, summaries, failed attempts, and
   audit events be retained?
4. Is multi-tenancy/case-level authorization required?
5. Is profile v1 observed-order metadata acceptable, or is a metadata prepass a
   correctness requirement for profile v2?
6. Should malformed datums drop, quarantine, or fail in production forensic
   workflows?
7. What are the required RPO/RTO and permitted family replay duration?
8. Which OIDC provider and deployment platform are authoritative?
9. Are physical source locations considered sensitive evidence?
10. Must analysis be reproducible across different CPU/runtime platforms at
    bit-identical floating-point level, or only within a supported platform?
11. Is more than one active run required, and how should priorities/quotas work?
12. What query patterns justify detail retention and database partitioning?

Until answered, choose conservative defaults: one active run per tenant/system,
one worker lane, family replay, bounded detail, opaque source IDs, strict
allowlisting, and no automatic production pruning.
