# Spectre.SemanticIndexing

Reusable, synchronous semantic behavioral indexing library. It consumes Layer 1
`GraphFact` records through `SemanticIndexingGraphFactSink` and emits compact,
closed event-time `SemanticGraphSlice` values.

## Data Flow

```text
GraphFact stream
  -> lightweight node metadata
  -> active tumbling-window documents and interactions
  -> post-window TF-IDF and exact Jaccard scoring
  -> ISemanticGraphSliceSink
```

The library does not retain raw edge history, build adjacency lists, reconstruct
a full graph, or implement disparity filtering. Closed windows are emitted and
evicted as the event-time watermark advances. Disposing the indexing sink flushes
remaining windows chronologically and disposes the owned slice sink.

`Spectre.DisparityFiltering` consumes these closed slices as Layer 3, consolidates
predicate interactions into directed source-target pair edges, and emits
slice-bounded disparity backbones.

## Integration

```csharp
using Spectre.SemanticIndexing.Sinks;

using IGraphFactSink sink = new SemanticIndexingGraphFactSink(
    new NullSemanticGraphSliceSink());
```

The sink is synchronous and not thread-safe. Edges without timestamps are
skipped, metadata affects only subsequently processed edges, and evidence
pointers are retained up to the configured per-interaction cap.

Late facts targeting already-emitted windows are skipped. Metrics include their
exact maximum lateness and bounded one-minute upper-bound estimates for P50,
P95, and P99 lateness so input-ordering and watermark policy can be evaluated
without retaining individual late events.

When used through `IngestionPipeline`, Layer 1 announces logical input-family
boundaries. The indexer flushes remaining windows and resets its watermark at
each boundary, so one family's event-time range cannot cause older events in a
later family to be skipped. Overlapping event-time windows are emitted once per
family and tagged with `InputFamilyBasePath`; downstream persistence or Layer 3
must keep that identity or explicitly merge matching family slices.

## Runtime Baseline

On June 14, 2026, the family-aware indexer processed the complete local CADETS
dataset through a null slice sink:

| Measurement | Result |
|---|---:|
| Input | 10 files, 3 families, 9.43 GiB |
| Wall-clock time | 8 minutes 39 seconds |
| Edge facts read | 39,015,609 |
| Late facts skipped | 0 |
| Documents emitted | 3,442,780 |
| Interactions emitted | 14,111,464 |
| Family-tagged slices emitted | 1,564 |

Persisting slices will add storage and serialization cost. Counts are higher
than a single global-watermark run because overlapping event-time windows are
emitted independently for each input family.
