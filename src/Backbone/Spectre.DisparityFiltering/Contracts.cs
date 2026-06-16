using Spectre.Indexing.Api;

namespace Spectre.DisparityFiltering;

/// <summary>Configuration for directed disparity filtering.</summary>
public sealed record DisparityFilterOptions
{
    /// <summary>Gets the strict significance threshold used to retain edges.</summary>
    public double Alpha { get; init; } = 0.05;

    /// <summary>Gets the maximum deduplicated evidence pointers retained per consolidated edge.</summary>
    public int MaxEvidencePointersPerEdge { get; init; } = 3;
}

/// <summary>Describes one edge's significance relative to one directed endpoint population.</summary>
public sealed record DirectionalDisparityScore
{
    /// <summary>Gets the number of distinct pair edges in the endpoint population.</summary>
    public required int Degree { get; init; }
    /// <summary>Gets the sum of semantic weights in the endpoint population.</summary>
    public required double Strength { get; init; }
    /// <summary>Gets this edge's fraction of the endpoint strength.</summary>
    public required double NormalizedWeight { get; init; }
    /// <summary>Gets the disparity p-value, or null when degree is one.</summary>
    public double? Significance { get; init; }
    /// <summary>Gets whether significance is strictly below the configured alpha.</summary>
    public required bool IsSignificant { get; init; }
}

/// <summary>Represents one retained directed source-target pair.</summary>
public sealed record BackboneInteraction
{
    /// <summary>Gets the source node identifier.</summary>
    public required Guid SourceNodeId { get; init; }
    /// <summary>Gets the target node identifier.</summary>
    public required Guid TargetNodeId { get; init; }
    /// <summary>Gets the inclusive event-time window start.</summary>
    public required long WindowStartNanos { get; init; }
    /// <summary>Gets the exclusive event-time window end.</summary>
    public required long WindowEndNanos { get; init; }
    /// <summary>Gets the checked sum of underlying event counts.</summary>
    public required int Count { get; init; }
    /// <summary>Gets the summed semantic weight used by the disparity filter.</summary>
    public required double SemanticWeight { get; init; }
    /// <summary>Gets event counts grouped by predicate.</summary>
    public required IReadOnlyDictionary<string, int> PredicateCounts { get; init; }
    /// <summary>Gets semantic weights grouped by predicate.</summary>
    public required IReadOnlyDictionary<string, double> PredicateSemanticWeights { get; init; }
    /// <summary>Gets merged semantic term counts.</summary>
    public required IReadOnlyDictionary<string, int> TermCounts { get; init; }
    /// <summary>Gets bounded, deduplicated supporting evidence.</summary>
    public required IReadOnlyList<EvidencePointer> Evidence { get; init; }
    /// <summary>Gets significance relative to the source's outgoing pair edges.</summary>
    public required DirectionalDisparityScore SourceOutgoing { get; init; }
    /// <summary>Gets significance relative to the target's incoming pair edges.</summary>
    public required DirectionalDisparityScore TargetIncoming { get; init; }
}

/// <summary>Describes the reduction performed for one emitted slice.</summary>
public sealed record DisparitySliceReduction
{
    /// <summary>Gets the configured significance threshold.</summary>
    public required double Alpha { get; init; }
    /// <summary>Gets the number of source semantic documents.</summary>
    public required int SourceDocumentCount { get; init; }
    /// <summary>Gets the number of source predicate-level interactions.</summary>
    public required int SourceInteractionCount { get; init; }
    /// <summary>Gets the number of consolidated source-target candidates.</summary>
    public required int CandidateEdgeCount { get; init; }
    /// <summary>Gets the number of retained endpoint documents.</summary>
    public required int RetainedDocumentCount { get; init; }
    /// <summary>Gets the number of retained backbone edges.</summary>
    public required int RetainedEdgeCount { get; init; }
    /// <summary>Gets total source semantic weight.</summary>
    public required double SourceSemanticWeight { get; init; }
    /// <summary>Gets total retained semantic weight.</summary>
    public required double RetainedSemanticWeight { get; init; }
}

/// <summary>Contains one closed disparity-filtered family/window backbone.</summary>
public sealed record DisparityGraphSlice
{
    /// <summary>Gets the source family base path.</summary>
    public string? InputFamilyBasePath { get; init; }
    /// <summary>Gets the inclusive event-time window start.</summary>
    public required long WindowStartNanos { get; init; }
    /// <summary>Gets the exclusive event-time window end.</summary>
    public required long WindowEndNanos { get; init; }
    /// <summary>Gets documents for retained edge endpoints only.</summary>
    public required IReadOnlyList<BehavioralDocument> Documents { get; init; }
    /// <summary>Gets retained consolidated backbone edges.</summary>
    public required IReadOnlyList<BackboneInteraction> Interactions { get; init; }
    /// <summary>Gets the upstream indexing metrics snapshot.</summary>
    public required SemanticIndexingMetrics IndexingMetrics { get; init; }
    /// <summary>Gets reduction statistics for this slice.</summary>
    public required DisparitySliceReduction Reduction { get; init; }
    /// <summary>Gets a cumulative filtering metrics snapshot taken for this emission.</summary>
    public required DisparityFilteringMetrics Metrics { get; init; }
}

/// <summary>Cumulative counters and timestamps for one disparity-filtering run.</summary>
public sealed class DisparityFilteringMetrics
{
    private long _sourceDocuments;
    private long _sourceInteractions;
    private long _candidateEdges;
    private long _retainedDocuments;
    private long _retainedEdges;
    private double _sourceSemanticWeight;
    private double _retainedSemanticWeight;
    private long _slicesEmitted;
    private long _processingStartedAtTicks;
    private long _processingEndedAtTicks;

    /// <summary>Gets the number of source documents read.</summary>
    public long SourceDocuments
    {
        get => Interlocked.Read(ref _sourceDocuments);
        internal set => Interlocked.Exchange(ref _sourceDocuments, value);
    }
    /// <summary>Gets the number of predicate-level source interactions read.</summary>
    public long SourceInteractions
    {
        get => Interlocked.Read(ref _sourceInteractions);
        internal set => Interlocked.Exchange(ref _sourceInteractions, value);
    }
    /// <summary>Gets the number of consolidated pair candidates evaluated.</summary>
    public long CandidateEdges
    {
        get => Interlocked.Read(ref _candidateEdges);
        internal set => Interlocked.Exchange(ref _candidateEdges, value);
    }
    /// <summary>Gets the number of endpoint documents retained.</summary>
    public long RetainedDocuments
    {
        get => Interlocked.Read(ref _retainedDocuments);
        internal set => Interlocked.Exchange(ref _retainedDocuments, value);
    }
    /// <summary>Gets the number of backbone edges retained.</summary>
    public long RetainedEdges
    {
        get => Interlocked.Read(ref _retainedEdges);
        internal set => Interlocked.Exchange(ref _retainedEdges, value);
    }
    /// <summary>Gets total semantic weight evaluated.</summary>
    public double SourceSemanticWeight
    {
        get => Interlocked.CompareExchange(ref _sourceSemanticWeight, 0, 0);
        internal set => Interlocked.Exchange(ref _sourceSemanticWeight, value);
    }
    /// <summary>Gets total semantic weight retained.</summary>
    public double RetainedSemanticWeight
    {
        get => Interlocked.CompareExchange(ref _retainedSemanticWeight, 0, 0);
        internal set => Interlocked.Exchange(ref _retainedSemanticWeight, value);
    }
    /// <summary>Gets the number of slices emitted, including empty backbones.</summary>
    public long SlicesEmitted
    {
        get => Interlocked.Read(ref _slicesEmitted);
        internal set => Interlocked.Exchange(ref _slicesEmitted, value);
    }
    /// <summary>Gets the UTC time processing started.</summary>
    public DateTimeOffset ProcessingStartedAt
    {
        get => new(Interlocked.Read(ref _processingStartedAtTicks), TimeSpan.Zero);
        internal set => Interlocked.Exchange(ref _processingStartedAtTicks, value.UtcTicks);
    }
    /// <summary>Gets the UTC time successful disposal completed.</summary>
    public DateTimeOffset? ProcessingEndedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _processingEndedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        internal set => Interlocked.Exchange(ref _processingEndedAtTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>Creates a thread-safe point-in-time snapshot.</summary>
    public DisparityFilteringMetrics Snapshot() => new()
    {
        SourceDocuments = SourceDocuments,
        SourceInteractions = SourceInteractions,
        CandidateEdges = CandidateEdges,
        RetainedDocuments = RetainedDocuments,
        RetainedEdges = RetainedEdges,
        SourceSemanticWeight = SourceSemanticWeight,
        RetainedSemanticWeight = RetainedSemanticWeight,
        SlicesEmitted = SlicesEmitted,
        ProcessingStartedAt = ProcessingStartedAt,
        ProcessingEndedAt = ProcessingEndedAt
    };
}
