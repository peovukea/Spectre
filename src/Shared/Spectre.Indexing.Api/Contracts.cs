using Spectre.Ingestion.Api;

namespace Spectre.Indexing.Api;

/// <summary>Configuration for fixed-window semantic behavioral indexing.</summary>
public sealed record SemanticIndexingOptions
{
    /// <summary>Gets the fixed tumbling-window duration.</summary>
    public TimeSpan WindowSize { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the event-time lateness allowed before windows close.</summary>
    public TimeSpan AllowedLateness { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the maximum evidence pointers retained per interaction.</summary>
    public int MaxEvidencePointersPerInteraction { get; init; } = 3;
}

/// <summary>Identifies one node behavioral document in one event-time window.</summary>
public sealed record DocumentKey(Guid NodeId, long WindowStartNanos);

/// <summary>Points to one source event supporting an interaction.</summary>
public sealed record EvidencePointer(SourceLocation Source, long? TimestampNanos, Guid? EventId);

/// <summary>Represents one node's behavior in one closed event-time window.</summary>
public sealed record BehavioralDocument
{
    public required DocumentKey Key { get; init; }
    public required Guid NodeId { get; init; }
    public required long WindowStartNanos { get; init; }
    public required long WindowEndNanos { get; init; }
    public required string NodeKind { get; init; }
    public required IReadOnlyDictionary<string, int> TermCounts { get; init; }
    public required IReadOnlyDictionary<string, double> TfidfWeights { get; init; }
    public double? JaccardToNodeKindBaseline { get; init; }
    public double? JaccardToPreviousSelf { get; init; }
}

/// <summary>Aggregates semantically weighted directed edges in one event-time window.</summary>
public sealed record WeightedInteraction
{
    public required Guid SourceNodeId { get; init; }
    public required Guid TargetNodeId { get; init; }
    public required long WindowStartNanos { get; init; }
    public required long WindowEndNanos { get; init; }
    public required string Predicate { get; init; }
    public required int Count { get; init; }
    public required double SemanticWeight { get; init; }
    public required IReadOnlyDictionary<string, int> TermCounts { get; init; }
    public required IReadOnlyList<EvidencePointer> Evidence { get; init; }
}

/// <summary>Contains all semantic documents and interactions from one closed event-time window.</summary>
public sealed record SemanticGraphSlice
{
    public string? InputFamilyBasePath { get; init; }
    public required long WindowStartNanos { get; init; }
    public required long WindowEndNanos { get; init; }
    public required IReadOnlyList<BehavioralDocument> Documents { get; init; }
    public required IReadOnlyList<WeightedInteraction> Interactions { get; init; }
    public required SemanticIndexingMetrics Metrics { get; init; }
}

/// <summary>Cumulative counters and UTC timestamps for one semantic indexing run.</summary>
public sealed class SemanticIndexingMetrics
{
    private long _factsRead;
    private long _edgeFactsRead;
    private long _attributeFactsRead;
    private long _documentsCreated;
    private long _documentsClosed;
    private long _interactionsCreated;
    private long _interactionsClosed;
    private long _factsSkippedWithoutTimestamp;
    private long _lateFactsSkipped;
    private long _lateFactLatenessMaxNanos;
    private long _lateFactLatenessP50Nanos;
    private long _lateFactLatenessP95Nanos;
    private long _lateFactLatenessP99Nanos;
    private long _slicesEmitted;
    private long _unknownNodeKindUses;
    private long _processingStartedAtTicks;
    private long _processingEndedAtTicks;

    public long FactsRead
    {
        get => Interlocked.Read(ref _factsRead);
        set => Interlocked.Exchange(ref _factsRead, value);
    }

    public long EdgeFactsRead
    {
        get => Interlocked.Read(ref _edgeFactsRead);
        set => Interlocked.Exchange(ref _edgeFactsRead, value);
    }

    public long AttributeFactsRead
    {
        get => Interlocked.Read(ref _attributeFactsRead);
        set => Interlocked.Exchange(ref _attributeFactsRead, value);
    }

    public long DocumentsCreated
    {
        get => Interlocked.Read(ref _documentsCreated);
        set => Interlocked.Exchange(ref _documentsCreated, value);
    }

    public long DocumentsClosed
    {
        get => Interlocked.Read(ref _documentsClosed);
        set => Interlocked.Exchange(ref _documentsClosed, value);
    }

    public long InteractionsCreated
    {
        get => Interlocked.Read(ref _interactionsCreated);
        set => Interlocked.Exchange(ref _interactionsCreated, value);
    }

    public long InteractionsClosed
    {
        get => Interlocked.Read(ref _interactionsClosed);
        set => Interlocked.Exchange(ref _interactionsClosed, value);
    }

    public long FactsSkippedWithoutTimestamp
    {
        get => Interlocked.Read(ref _factsSkippedWithoutTimestamp);
        set => Interlocked.Exchange(ref _factsSkippedWithoutTimestamp, value);
    }

    public long LateFactsSkipped
    {
        get => Interlocked.Read(ref _lateFactsSkipped);
        set => Interlocked.Exchange(ref _lateFactsSkipped, value);
    }

    public long LateFactLatenessMaxNanos
    {
        get => Interlocked.Read(ref _lateFactLatenessMaxNanos);
        set => Interlocked.Exchange(ref _lateFactLatenessMaxNanos, value);
    }

    public long LateFactLatenessP50Nanos
    {
        get => Interlocked.Read(ref _lateFactLatenessP50Nanos);
        set => Interlocked.Exchange(ref _lateFactLatenessP50Nanos, value);
    }

    public long LateFactLatenessP95Nanos
    {
        get => Interlocked.Read(ref _lateFactLatenessP95Nanos);
        set => Interlocked.Exchange(ref _lateFactLatenessP95Nanos, value);
    }

    public long LateFactLatenessP99Nanos
    {
        get => Interlocked.Read(ref _lateFactLatenessP99Nanos);
        set => Interlocked.Exchange(ref _lateFactLatenessP99Nanos, value);
    }

    public long SlicesEmitted
    {
        get => Interlocked.Read(ref _slicesEmitted);
        set => Interlocked.Exchange(ref _slicesEmitted, value);
    }

    public long UnknownNodeKindUses
    {
        get => Interlocked.Read(ref _unknownNodeKindUses);
        set => Interlocked.Exchange(ref _unknownNodeKindUses, value);
    }

    public DateTimeOffset ProcessingStartedAt
    {
        get => new DateTimeOffset(Interlocked.Read(ref _processingStartedAtTicks), TimeSpan.Zero);
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

    public SemanticIndexingMetrics Snapshot() => new()
    {
        FactsRead = FactsRead,
        EdgeFactsRead = EdgeFactsRead,
        AttributeFactsRead = AttributeFactsRead,
        DocumentsCreated = DocumentsCreated,
        DocumentsClosed = DocumentsClosed,
        InteractionsCreated = InteractionsCreated,
        InteractionsClosed = InteractionsClosed,
        FactsSkippedWithoutTimestamp = FactsSkippedWithoutTimestamp,
        LateFactsSkipped = LateFactsSkipped,
        LateFactLatenessMaxNanos = LateFactLatenessMaxNanos,
        LateFactLatenessP50Nanos = LateFactLatenessP50Nanos,
        LateFactLatenessP95Nanos = LateFactLatenessP95Nanos,
        LateFactLatenessP99Nanos = LateFactLatenessP99Nanos,
        SlicesEmitted = SlicesEmitted,
        UnknownNodeKindUses = UnknownNodeKindUses,
        ProcessingStartedAt = ProcessingStartedAt,
        ProcessingEndedAt = ProcessingEndedAt
    };
}
