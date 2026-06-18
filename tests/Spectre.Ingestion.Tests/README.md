# Spectre.Ingestion.Tests

Automated tests for family preflight validation, lazy Avro reading, normalized
datum projection, sink behavior, metrics, cancellation, and UUID conversion.

Run from the repository root:

```powershell
dotnet test Spectre.Ingestion.slnx
```

Tests create deterministic Avro fixtures at runtime using the committed CDM18
specific-record classes and `DataFileWriter<TCCDMDatum>`. Production CADETS
data is not required and is not modified.
