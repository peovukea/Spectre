using Spectre.Indexing.Api;

namespace Spectre.Backbone.Api;

/// <summary>Configuration for directed disparity filtering.</summary>
public sealed record DisparityFilterOptions
{
    public double Alpha { get; init; } = 0.05;
    public int MaxEvidencePointersPerEdge { get; init; } = 3;
}

/// <summary>Describes one edge's significance relative to one directed endpoint population.</summary>
public sealed record DirectionalDisparityScore
{
    public required int Degree { get; init; }
    public required double Strength { get; init; }
    public required double NormalizedWeight { get; init; }
    public double? Significance { get; init; }
    public required bool IsSignificant { get; init; }
}

/// <summary>Represents one retained directed source-target pair.</summary>
public sealed record BackboneInteraction
{
    public required Guid SourceNodeId { get; init; }
    public required Guid TargetNodeId { get; init; }
    public required long WindowStartNanos { get; init; }
    public required long WindowEndNanos { get; init; }
    public required int Count { get; init; }
    public required double SemanticWeight { get; init; }
    public required IReadOnlyDictionary<string, int> PredicateCounts { get; init; }
    public required IReadOnlyDictionary<string, double> PredicateSemanticWeights { get; init; }
    public required IReadOnlyDictionary<string, int> TermCounts { get; init; }
    public required IReadOnlyList<EvidencePointer> Evidence { get; init; }
    public required DirectionalDisparityScore SourceOutgoing { get; init; }
    public required DirectionalDisparityScore TargetIncoming { get; init; }
}

/// <summary>Describes the reduction performed for one emitted slice.</summary>
public sealed record DisparitySliceReduction
{
    public required double Alpha { get; init; }
    public required int SourceDocumentCount { get; init; }
    public required int SourceInteractionCount { get; init; }
    public required int CandidateEdgeCount { get; init; }
    public required int RetainedDocumentCount { get; init; }
    public required int RetainedEdgeCount { get; init; }
    public required double SourceSemanticWeight { get; init; }
    public required double RetainedSemanticWeight { get; init; }
}

/// <summary>Contains one closed disparity-filtered family/window backbone.</summary>
public sealed record DisparityGraphSlice
{
    public string? InputFamilyBasePath { get; init; }
    public required long WindowStartNanos { get; init; }
    public required long WindowEndNanos { get; init; }
    public required IReadOnlyList<BehavioralDocument> Documents { get; init; }
    public required IReadOnlyList<BackboneInteraction> Interactions { get; init; }
    public required SemanticIndexingMetrics IndexingMetrics { get; init; }
    public required DisparitySliceReduction Reduction { get; init; }
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

    public long SourceDocuments
    {
        get => Interlocked.Read(ref _sourceDocuments);
        set => Interlocked.Exchange(ref _sourceDocuments, value);
    }

    public long SourceInteractions
    {
        get => Interlocked.Read(ref _sourceInteractions);
        set => Interlocked.Exchange(ref _sourceInteractions, value);
    }

    public long CandidateEdges
    {
        get => Interlocked.Read(ref _candidateEdges);
        set => Interlocked.Exchange(ref _candidateEdges, value);
    }

    public long RetainedDocuments
    {
        get => Interlocked.Read(ref _retainedDocuments);
        set => Interlocked.Exchange(ref _retainedDocuments, value);
    }

    public long RetainedEdges
    {
        get => Interlocked.Read(ref _retainedEdges);
        set => Interlocked.Exchange(ref _retainedEdges, value);
    }

    public double SourceSemanticWeight
    {
        get => Interlocked.CompareExchange(ref _sourceSemanticWeight, 0, 0);
        set => Interlocked.Exchange(ref _sourceSemanticWeight, value);
    }

    public double RetainedSemanticWeight
    {
        get => Interlocked.CompareExchange(ref _retainedSemanticWeight, 0, 0);
        set => Interlocked.Exchange(ref _retainedSemanticWeight, value);
    }

    public long SlicesEmitted
    {
        get => Interlocked.Read(ref _slicesEmitted);
        set => Interlocked.Exchange(ref _slicesEmitted, value);
    }

    public DateTimeOffset ProcessingStartedAt
    {
        get => new(Interlocked.Read(ref _processingStartedAtTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _processingStartedAtTicks, value.UtcTicks);
    }

    public DateTimeOffset? ProcessingEndedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _processingEndedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        set => Interlocked.Exchange(ref _processingEndedAtTicks, value?.UtcTicks ?? 0);
    }

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
