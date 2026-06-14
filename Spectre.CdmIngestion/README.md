# Spectre.CdmIngestion

Reusable, synchronous CDM18 Avro ingestion library. It validates and orders
CADETS input families, lazily reads specific-record Avro containers, normalizes
records, projects typed graph facts, and writes them to an `IGraphFactSink`.

## Pipeline

1. `CdmInputFamilyDiscovery` expands and validates every input before ingestion.
2. `DarpaCdm18AvroReader` streams `SourcedCdmDatum` values from each segment.
3. `GraphFactProjector` converts supported datums into `GraphFact` values.
4. `CdmIngestionPipeline` writes facts and records partial or completed metrics.

The library does not build or retain graph state. Enumeration and projection
remain lazy, and the pipeline checks cancellation between datums and sink writes.

## Main Contracts

- `ICdmRecordReader`: lazily reads normalized, source-located CDM datums.
- `IGraphFactProjector`: projects one normalized datum at a time.
- `IGraphFactSink`: accepts typed `EdgeFact` and `AttributeFact` values.
- `CdmIngestionResult`: reports outcome, metrics, and an optional failure.
- `SourceLocation`: identifies a physical segment and Avro sync-block offset.

## Input Families

A family consists of a base segment and optional numbered continuations:

```text
official.bin
official.bin.1
official.bin.2
```

Pass directories or base `.bin` paths as inputs. Direct `.bin.N` inputs,
missing bases, gaps, duplicate segments, and duplicate physical families fail
preflight validation before any Avro file or sink is opened.

## Generated CDM18 Records

Specific-record classes are committed under `Generated/Cdm18`. Regenerate them
explicitly from the repository root:

```powershell
dotnet run --project tools/GenerateCdm18
```

Normal builds do not regenerate these files. XML documentation warnings from
generated classes are suppressed because schema documentation may not be valid
C# XML documentation; hand-authored public APIs remain documented.
