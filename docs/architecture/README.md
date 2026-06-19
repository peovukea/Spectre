# Spectre Greenfield Architecture

## Purpose

This documentation describes how Spectre should be rebuilt from a clean slate
while preserving the valuable behavior already proven by the current system.
It is a target design, not a description of code that already exists.

The design optimizes for:

- deterministic analysis results;
- bounded memory on multi-gigabyte CDM input families;
- durable, restartable processing;
- explicit backpressure and cancellation;
- independently scalable API and processing workloads;
- fast, bounded investigation queries;
- strong observability and operational recovery;
- clean dependency direction and replaceable infrastructure;
- automated verification of correctness, compatibility, and performance.

## Executive Decision

Build a modular monolith with two .NET process roles and one web frontend:

1. `Spectre.Api` handles authenticated commands, queries, OpenAPI, and live
   event delivery. It never performs analysis work in an HTTP request.
2. `Spectre.Worker` claims durable PostgreSQL jobs, processes families through
   the CDM, semantic-indexing, and disparity stages, and persists results.
3. `Spectre.Web` is a Next.js App Router application. Server Components provide
   the initial read view; small Client Component islands own filters, graph
   interaction, and server-sent event reconciliation.
4. PostgreSQL is the durable run ledger, work queue, attempt store, read model,
   and event outbox. Do not add a message broker until measured scale requires
   one.

This is not a microservice architecture. API and worker are separate process
roles because they have different lifetime, scaling, and failure requirements,
but they share one application core and one database boundary.

## Target Context

```text
Browser
  |
  | HTTPS, JSON, SSE
  v
Next.js web/BFF  --->  ASP.NET Core API  --->  PostgreSQL
                              ^                    ^
                              | commands/queries   | jobs, leases,
                              |                    | attempts, results,
                              |                    | event outbox
                              |                    |
                              +-------------- Spectre Worker
                                                   |
                                                   v
                                          allowlisted CDM inputs
```

## Documentation Map

| Document | Question answered |
|---|---|
| [Current-state assessment](01-current-state-assessment.md) | What exists, what is sound, and what should change? |
| [Target architecture](02-target-architecture.md) | What are the projects, modules, dependencies, and deployables? |
| [Pipeline design](03-pipeline-design.md) | How does data move correctly and efficiently through the system? |
| [Data and storage](04-data-and-storage.md) | How are jobs, attempts, results, and queries persisted? |
| [API and frontend](05-api-and-frontend.md) | How do HTTP contracts, SSE, and Next.js fit together? |
| [Operations and security](06-operations-security-observability.md) | How is the system run, secured, observed, and recovered? |
| [Quality and performance](07-quality-testing-performance.md) | What standards, tests, CI gates, and budgets prove quality? |
| [Migration roadmap](08-migration-roadmap.md) | How can the current implementation be replaced without a blind rewrite? |
| [Architecture decisions](09-architecture-decisions.md) | Which major choices are fixed, rejected, or still open? |
| [Analysis profile v1](10-analysis-profile-v1.md) | What exact analytical behavior must the rebuild preserve? |

## Architectural Principles

### Dependencies point inward

Business rules and analysis algorithms do not depend on ASP.NET Core, Npgsql,
Apache Avro, the filesystem, JSON serialization, or Next.js. Infrastructure
implements ports owned by the application core. The API and worker are
composition roots, not locations for business logic.

### Durability before distribution

A process crash must not lose the existence, configuration, cancellation state,
or last completed unit of a run. PostgreSQL provides this durability first. A
broker or more services would add operational cost without solving a current
measured bottleneck.

### Bounded work everywhere

Every collection, queue, query, response, evidence list, and concurrency setting
has an explicit limit. Producers wait when consumers are saturated. The system
does not exchange unbounded `IEnumerable` chains between owners and does not
create a task per datum.

### Determinism is a product feature

The same immutable input manifest, analysis profile, schema version, and build
must produce the same persisted result hashes regardless of worker count. Any
parallel stage must preserve this invariant.

### Replay instead of fragile fine-grained snapshots

Family state includes metadata, watermark state, document frequency, baselines,
and open windows. Persisting that internal state after every Avro block would
couple checkpoints to implementation details. A family attempt is therefore the
default replay unit. Failed attempts remain invisible; a successful attempt is
promoted atomically.

### External contracts are not domain models

HTTP DTOs are versioned at the boundary. Domain records express invariants and
use .NET types appropriate to the core. OpenAPI is the source for generated
frontend clients and contract tests.

### Measure before optimizing

The current implementation demonstrates that streaming can process the 9.43 GiB
reference set in roughly nine minutes. The rebuild must preserve that baseline
before adding concurrency. Allocation, CPU, I/O, database, and query profiles
decide where optimization work is justified.

## Required Quality Attributes

| Attribute | Initial target |
|---|---|
| Correctness | Golden-result equivalence for the existing reference fixtures; deterministic repeat runs |
| Input scale | At least 20 GiB and 100 million source records per run without unbounded memory growth |
| Worker memory | Configured hard operating budget; initial target 2 GiB per worker lane, validated by load tests |
| Throughput | At least the current reference throughput before enabling parallel families |
| Recovery | API restart loses no job; worker crash requeues an expired family lease and hides failed-attempt output |
| Cancellation | Durable cancellation request visible within 5 seconds; cooperative stop at safe batch boundaries |
| Query latency | P95 under 500 ms for bounded graph queries on the reference dataset |
| Availability | Query API remains responsive while workers ingest at full rate |
| Compatibility | OpenAPI breaking-change check and generated TypeScript client in CI |
| Security | Authenticated run control, authorized read access, allowlisted inputs, no physical paths in public DTOs |

Targets are provisional until benchmark hardware and production concurrency are
recorded. They must become executable performance gates rather than aspirations.

## Deliberate Non-Goals

- Do not introduce Kafka, RabbitMQ, Kubernetes, or service-to-service RPC in the
  first rebuild.
- Do not persist every raw graph fact by default. The immutable source data is
  the replay source unless a later requirement justifies a durable fact lake.
- Do not support arbitrary filesystem paths supplied by remote callers.
- Do not promise record-level resume when Apache Avro exposes a sync-block
  location and semantic state is family-scoped.
- Do not make the analysis algorithms dependent on database schemas or HTTP
  response shapes.
- Do not optimize with pools, unsafe code, custom serializers, or broad
  parallelism without benchmark evidence.

## Source Guidance

The design follows Microsoft guidance on [architectural principles](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/architectural-principles),
[Clean Architecture](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures#clean-architecture),
[hosted background services](https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0),
[health checks](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0),
[OpenTelemetry](https://learn.microsoft.com/dotnet/core/diagnostics/observability-with-otel),
[ASP.NET Core performance](https://learn.microsoft.com/aspnet/core/performance/performance-best-practices?view=aspnetcore-10.0),
and [integration testing](https://learn.microsoft.com/aspnet/core/test/integration-tests?view=aspnetcore-10.0).

The frontend recommendations use the official Next.js 16.2.9 guidance for
[Server and Client Components](https://github.com/vercel/next.js/blob/v16.2.9/docs/01-app/01-getting-started/05-server-and-client-components.mdx),
[data security](https://github.com/vercel/next.js/blob/v16.2.9/docs/01-app/02-guides/data-security.mdx),
and the [production checklist](https://github.com/vercel/next.js/blob/v16.2.9/docs/01-app/02-guides/production-checklist.mdx).
