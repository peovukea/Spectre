# Target Architecture

## Architecture Style

Use Clean Architecture inside a modular monolith, with two independently
deployable .NET process roles. The code is organized by stable business
capabilities and dependency direction, not by the order in which sinks call one
another.

This gives the project:

- one source of truth for domain concepts;
- replaceable filesystem, Avro, database, clock, and event-delivery adapters;
- independent API and worker scaling;
- in-process calls for analysis performance;
- no distributed transaction or broker burden;
- a clear path to extract a service later if measurements justify it.

## Logical Layers

```text
                     Composition roots
                 +------------------------+
                 | Spectre.Api            |
                 | Spectre.Worker         |
                 +-----------+------------+
                             |
            +----------------+----------------+
            | Infrastructure adapters         |
            | PostgreSQL | Avro | filesystem  |
            | clock | outbox | telemetry      |
            +----------------+----------------+
                             |
                 +-----------+------------+
                 | Application use cases  |
                 | commands, queries, jobs|
                 +-----------+------------+
                             |
                 +-----------+------------+
                 | Domain and algorithms  |
                 | facts, windows, scores |
                 +------------------------+
```

Compile-time references point downward toward the domain. Runtime control can
flow outward through interfaces owned by the application layer.

## Recommended Solution Layout

Keep project count deliberate. The following structure separates concerns that
have distinct dependencies or test strategies without creating one assembly per
folder.

```text
src/
  Spectre.Domain/
  Spectre.Application/
  Spectre.Analysis/
  Spectre.Infrastructure.Avro/
  Spectre.Infrastructure.Postgres/
  Spectre.Api/
  Spectre.Worker/

contracts/
  Spectre.HttpContracts/

tools/
  Spectre.SchemaGenerator/
  Spectre.Benchmarks/

tests/
  Spectre.Domain.Tests/
  Spectre.Analysis.Tests/
  Spectre.Application.Tests/
  Spectre.Avro.IntegrationTests/
  Spectre.Postgres.IntegrationTests/
  Spectre.Api.FunctionalTests/
  Spectre.Architecture.Tests/
  Spectre.EndToEnd.Tests/

frontend/
  app/
  features/
  components/
  lib/api/
  lib/contracts/generated/
```

### `Spectre.Domain`

Owns stable, infrastructure-free concepts and invariants:

- run, family, segment, attempt, analysis profile, and version identifiers;
- input manifest value objects and source evidence identities;
- normalized CDM datum and graph fact discriminated models;
- event-time window keys and watermark decisions;
- behavioral documents, weighted interactions, and backbone slices;
- disparity scores and reduction summaries;
- domain errors and state-transition rules.

This project has no package references other than carefully justified BCL-only
dependencies. It does not contain DTO suffixes, database attributes, JSON
converters, loggers, or framework configuration.

### `Spectre.Application`

Owns use cases and ports:

- create and validate a run;
- request cancellation;
- claim, renew, complete, or fail a family attempt;
- orchestrate one family analysis;
- promote a completed attempt;
- query runs, families, windows, projections, nodes, and interactions;
- publish durable application events;
- apply retention policies.

It defines interfaces for job storage, result writing, read-model queries,
manifest discovery, input opening, time, identity generation, transactions, and
event publication. It depends on `Spectre.Domain` only.

### `Spectre.Analysis`

Owns pure or near-pure business algorithms:

- datum-to-fact projection;
- metadata resolution and semantic term extraction;
- event-time window assignment and watermark policy;
- TF-IDF and Jaccard calculation;
- interaction consolidation;
- directed disparity scoring;
- deterministic ordering and result hashing;
- internal mutable accumulators and immutable output snapshots.

It depends on `Spectre.Domain`. It must not reference Apache Avro, Npgsql,
ASP.NET Core, filesystem APIs, HTTP DTOs, or UI types.

### `Spectre.Infrastructure.Avro`

Owns all CDM18 and filesystem details:

- input root allowlisting and canonicalization;
- family/segment discovery and manifest fingerprints;
- Apache Avro object-container opening and schema validation;
- generated CDM18 specific records;
- static/generated normalization maps;
- sync-block source positions and classified input errors.

Generated classes are isolated here so their namespace, warning policy, and
regeneration tool do not leak into domain or application projects.

### `Spectre.Infrastructure.Postgres`

Owns durable adapters:

- migrations;
- job and lease repository;
- attempt writer and atomic promotion;
- binary bulk import;
- read-model queries;
- durable event outbox;
- cleanup and retention jobs;
- PostgreSQL health checks.

Use Npgsql directly for high-volume COPY and query paths. EF Core migrations may
remain a schema-management choice, but an empty `DbContext` should not exist
only to host raw SQL without an explicit rationale.

### `Spectre.HttpContracts`

Owns the externally versioned HTTP request and response shapes only. It contains
no infrastructure references and does not expose mutable internal metric
objects. The OpenAPI document generated from this boundary is the source of the
TypeScript client.

Consider keeping this as a namespace in `Spectre.Api` until another .NET client
actually needs the assembly. A separate project is justified only by a real
consumer or contract-generation boundary.

### `Spectre.Api`

Owns:

- authentication and authorization;
- versioned endpoints;
- request validation and Problem Details mapping;
- command acceptance and query mapping;
- OpenAPI;
- SSE connections and outbox replay;
- HTTP-specific caching, rate limiting, and telemetry;
- the API composition root.

It does not open Avro files, execute analysis, own run timers, or contain SQL.

### `Spectre.Worker`

Owns:

- the worker composition root;
- durable claim/lease loops;
- graceful shutdown;
- bounded processing lane configuration;
- transient retry policy;
- progress heartbeat and checkpoint publication;
- cleanup of abandoned attempts.

It composes application use cases with Avro and PostgreSQL adapters. It contains
no algorithm implementation and exposes no public HTTP API beyond health and
metrics if the deployment platform requires them.

## Dependency Matrix

| Project | May reference |
|---|---|
| Domain | BCL only |
| Application | Domain |
| Analysis | Domain |
| Infrastructure.Avro | Domain, Application, Apache Avro |
| Infrastructure.Postgres | Domain, Application, Npgsql and migration tooling |
| HttpContracts | BCL serialization annotations only when unavoidable |
| Api | Application, HttpContracts, DI registration entry points |
| Worker | Application, Analysis, infrastructure DI registration entry points |

Architecture tests must reject reverse references, infrastructure namespaces in
core projects, and concrete adapter construction outside composition roots.

## Domain Boundaries

### Run Management

Owns run lifecycle, immutable configuration, cancellation intent, progress,
attempt history, algorithm/schema versions, and terminal outcome.

### Input Catalog

Owns allowlisted input sources, immutable manifests, family identities, segment
ordering, file metadata, and fingerprints. A public API accepts an input-source
identifier, never a raw server path.

### Analysis

Owns normalized facts, window semantics, semantic weighting, disparity
filtering, evidence, and deterministic output. It is versioned through an
immutable analysis profile.

### Investigation

Owns summaries, projections, details, filters, pagination, and retention state.
It reads promoted attempt data only.

These are modules inside one bounded context. Do not split them into networked
services unless independent scaling, team ownership, or data sovereignty later
provides a concrete reason.

## Process Topology

### Development

Run PostgreSQL, API, worker, and Next.js as four local processes/containers. A
single Compose file supplies health checks and persistent development data. Use
separate development credentials and never copy them into production defaults.

### Production

Minimum topology:

- two stateless API replicas behind a reverse proxy;
- one worker replica initially, with support for multiple family lanes;
- one PostgreSQL primary with backups and connection pooling;
- one or more Next.js replicas or a managed web deployment;
- an OpenTelemetry collector and metrics/log backends.

API scaling is independent of worker scaling. Multiple worker replicas become
safe because claims use leases and fencing tokens. Database connection budgets
are assigned per role so workers cannot starve API queries.

## Composition Rules

1. `Program` files perform configuration binding, validation, dependency
   registration, middleware ordering, and host startup only.
2. Each infrastructure project exposes one registration entry point. The API
   and worker do not instantiate concrete adapters throughout business code.
3. Option records are immutable, validated at startup, and snapshotted into a
   run. A running job never changes behavior because configuration reloaded.
4. Dependencies use constructor injection. Static helpers are limited to pure,
   context-free functions.
5. Time, filesystem, randomness, process identity, and external I/O are behind
   ports where deterministic tests need control.
6. Resource ownership belongs to the composition root or a clear operation
   scope. A processing stage never silently disposes a downstream collaborator.

## Contract Design

### Internal messages

Every pipeline envelope carries stable context:

- run identifier;
- family identifier;
- attempt identifier and fencing token;
- segment ordinal and source position;
- monotonic sequence within the family;
- schema version;
- analysis-profile version.

Use immutable records at boundaries. Mutable dictionaries and counters remain
private to accumulators. Publish immutable metric snapshots rather than mutable
thread-safe counter objects.

### Errors

Use a small error taxonomy:

- validation: caller or manifest is invalid;
- input: unreadable, truncated, wrong schema, or changed source;
- semantic: deterministic analysis invariant violation;
- transient infrastructure: retryable database or storage failure;
- permanent infrastructure: configuration, permission, or capacity failure;
- canceled: operator-requested cooperative stop;
- abandoned: lease expired because a worker disappeared.

The taxonomy determines retry and final state. Do not catch all exceptions and
reduce them to a single failed status without a durable reason code.

## Naming and Visibility

- Use names from the domain: `InputManifest`, `FamilyAttempt`, `WindowKey`, and
  `BackboneSlice`, not generic `Manager`, `Helper`, or `Processor` names.
- Interfaces represent actual substitution boundaries, not every concrete type.
- Public surface is minimal. Most implementation types are internal.
- Async methods use the `Async` suffix and accept cancellation when they wait on
  I/O or bounded queues.
- IDs are strongly typed internally so a run ID cannot be passed as a family ID.
- Options describe immutable policy; runtime progress and metrics are separate.

These conventions align with Microsoft's [.NET naming guidance](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/identifier-names).
