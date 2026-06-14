# Spectre.CdmIngestion.Cli

Command-line host for the CDM Graph-Fact Ingestion library.

## Run

```powershell
dotnet run --project Spectre.CdmIngestion.Cli -- `
  --input data/cadets `
  --sample-output output/graphfacts.sample.jsonl `
  --sample-limit 50000
```

`--input` is repeatable and accepts a directory or a base `.bin` family path.
All inputs are validated together and processed in global ordinal base-path
order.

## Options

| Option | Description |
|---|---|
| `--input <path>` | Required, repeatable directory or base `.bin` path. |
| `--sample-output <path>` | JSONL sample path. Defaults to `output/graphfacts.sample.jsonl`. |
| `--sample-limit <count>` | Maximum sampled facts. Defaults to `50000`. |
| `--metrics-only` | Run ingestion without creating sample output. |
| `--help` | Print usage. |

The sample sink becomes a successful no-op after its cap. Metrics still count
all facts accepted by the top-level sink.

Press Ctrl+C for cooperative cancellation. The CLI flushes partial output,
prints partial metrics, and exits with code `130`. Validation, Avro, file, and
sink failures print partial metrics and exit non-zero.

## Output

Each JSONL object is a typed graph fact with source location flattened as
`sourceFile` and `sourceOffset`. The offset is an Avro sync-block location, not
an exact per-record byte offset.
