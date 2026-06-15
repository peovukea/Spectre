# Spectre.DisparityFiltering

Reusable synchronous Layer 3 disparity filtering. It consumes closed
`SemanticGraphSlice` values, consolidates predicate-level interactions into one
directed source-target pair edge, and emits statistically significant
`DisparityGraphSlice` backbones.

For every family/window slice, pair-edge semantic weights are compared against
the source's outgoing and target's incoming populations. An edge is retained
when either directional significance is strictly below the configured alpha.
Degree-one directions never automatically pass.

```csharp
using Spectre.DisparityFiltering.Sinks;
using Spectre.SemanticIndexing.Sinks;

using ISemanticGraphSliceSink sink = new DisparityFilteringSemanticGraphSliceSink(
    new NullDisparityGraphSliceSink());
```

The filter uses only slice-bounded temporary state, emits empty slices to
preserve the event-time timeline, and owns its downstream disparity slice sink.
