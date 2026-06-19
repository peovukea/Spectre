# CDM Graph-Fact Ingestion

## Greenfield Architecture

The detailed from-scratch target design, including pipeline durability,
PostgreSQL storage, API/frontend boundaries, security, performance, testing, and
migration, starts at [`docs/architecture/README.md`](docs/architecture/README.md).

This .NET 10 solution synchronously and lazily streams DARPA CDM18 Avro
object-container families into typed graph facts, semantic event-time windows,
disparity-filtered backbones, and a PostgreSQL-backed investigation API.

## Projects

| Project | Purpose |
|---|---|
| [`Spectre.Ingestion`](src/Ingestion/Spectre.Ingestion/README.md) | Reusable family discovery, Avro reader, normalized datum boundary, projector, sinks, and metrics. |
| [`Spectre.Ingestion.Tests`](tests/Spectre.Ingestion.Tests/README.md) | Unit and deterministic Avro object-container tests. |
| [`Spectre.SemanticIndexing`](src/Indexing/Spectre.SemanticIndexing/README.md) | Streaming Layer 2 behavioral documents, semantic interactions, TF-IDF, and exact Jaccard scoring. |
| [`Spectre.SemanticIndexing.Tests`](tests/Spectre.SemanticIndexing.Tests) | Focused handcrafted graph-fact tests for Layer 2. |
| [`Spectre.DisparityFiltering`](src/Backbone/Spectre.DisparityFiltering/README.md) | Slice-bounded Layer 3 directed disparity filtering and backbone extraction. |
| [`Spectre.DisparityFiltering.Tests`](tests/Spectre.DisparityFiltering.Tests) | Exact significance, consolidation, evidence, and lifecycle tests for Layer 3. |
| [`Spectre.InvestigationHost`](src/Investigation/Spectre.InvestigationHost/README.md) | PostgreSQL-backed API host for live latest-run backbone investigation. |
| [`Spectre.InvestigationHost.Tests`](tests/Spectre.InvestigationHost.Tests) | Focused backbone query-store integration tests. |
| [`tools/GenerateCdm18`](tools/GenerateCdm18/README.md) | Explicit CDM18 specific-record generation tool. |

## Data Flow

```text
input arguments
  -> global family discovery and validation
  -> lazy specific-record Avro reading
  -> sourced normalized datums
  -> typed GraphFact projection
  -> Layer 2 semantic family/window slices
  -> Layer 3 consolidated disparity backbones
  -> investigation sink
```

All inputs are validated before any Avro segment or output sink is opened.
Families use a base `<family>.bin` followed by contiguous optional segments
`<family>.bin.1`, `<family>.bin.2`, and so on. Families from all input arguments
are merged and processed in global ordinal base-path order.

## Build And Test

```powershell
dotnet build Spectre.slnx
dotnet test Spectre.slnx
```

## Run

Drive ingestion through the API host. The reusable ingestion library remains
available for composition inside the web host and tests.

## Runtime Baseline

On June 14, 2026, the Release metrics-only pipeline processed the complete
local CADETS dataset in this workspace:

| Measurement | Result |
|---|---:|
| Input | 10 files, 3 families, 9.43 GiB |
| Wall-clock time | 8 minutes 22 seconds |
| Records read | 44,404,339 |
| Facts accepted | 52,874,058 |
| Average input throughput | 19.3 MiB/s |
| Average record throughput | 88,500 records/s |

For this machine and dataset, plan on roughly **9 minutes** for a full
metrics-only or default capped-sample run. Allow **10-15 minutes** operationally
for storage contention, cold filesystem cache, antivirus scanning, or different
record mixes. An uncapped JSONL sink would be substantially slower and produce
very large output.

## Output And Metrics

Sample JSONL facts flatten source location as `sourceFile` and `sourceOffset`.
The offset identifies the previous Avro sync block, not an exact per-record byte
position.

The final metrics report includes records and facts processed, fact variants,
skipped and malformed records, completed files and families, and UTC processing
timestamps. Failed and canceled runs preserve and print partial metrics.

## Generated CDM18 Records

The committed files under `src/Ingestion/Spectre.Ingestion/Generated/Cdm18` are generated
from `TCCDMDatum.avsc`. Their CLR namespace intentionally matches the Avro
full-name namespace so Apache Avro specific-record resolution remains compatible
with the embedded writer schema.

Regeneration is an explicit developer action and is not part of normal builds:

```powershell
dotnet run --project tools/GenerateCdm18
```
