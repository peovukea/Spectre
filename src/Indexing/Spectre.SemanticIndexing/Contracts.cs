using Spectre.Ingestion.Api;

namespace Spectre.SemanticIndexing;

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
    /// <summary>Gets the document key.</summary>
    public required DocumentKey Key { get; init; }
    /// <summary>Gets the described node identifier.</summary>
    public required Guid NodeId { get; init; }
    /// <summary>Gets the inclusive event-time window start.</summary>
    public required long WindowStartNanos { get; init; }
    /// <summary>Gets the exclusive event-time window end.</summary>
    public required long WindowEndNanos { get; init; }
    /// <summary>Gets the node kind known when the document was created.</summary>
    public required string NodeKind { get; init; }
    /// <summary>Gets raw semantic term counts.</summary>
    public required IReadOnlyDictionary<string, int> TermCounts { get; init; }
    /// <summary>Gets finalized TF-IDF term weights.</summary>
    public required IReadOnlyDictionary<string, double> TfidfWeights { get; init; }
    /// <summary>Gets exact Jaccard overlap with the prior node-kind baseline.</summary>
    public double? JaccardToNodeKindBaseline { get; init; }
    /// <summary>Gets exact Jaccard overlap with the node's previous document.</summary>
    public double? JaccardToPreviousSelf { get; init; }
}

/// <summary>Aggregates semantically weighted directed edges in one event-time window.</summary>
public sealed record WeightedInteraction
{
    /// <summary>Gets the source node identifier.</summary>
    public required Guid SourceNodeId { get; init; }
    /// <summary>Gets the target node identifier.</summary>
    public required Guid TargetNodeId { get; init; }
    /// <summary>Gets the inclusive event-time window start.</summary>
    public required long WindowStartNanos { get; init; }
    /// <summary>Gets the exclusive event-time window end.</summary>
    public required long WindowEndNanos { get; init; }
    /// <summary>Gets the edge predicate.</summary>
    public required string Predicate { get; init; }
    /// <summary>Gets the number of aggregated edges.</summary>
    public required int Count { get; init; }
    /// <summary>Gets the positive semantic interaction weight.</summary>
    public required double SemanticWeight { get; init; }
    /// <summary>Gets raw interaction-level semantic term counts.</summary>
    public required IReadOnlyDictionary<string, int> TermCounts { get; init; }
    /// <summary>Gets capped supporting evidence in arrival order.</summary>
    public required IReadOnlyList<EvidencePointer> Evidence { get; init; }
}

/// <summary>Contains all semantic documents and interactions from one closed event-time window.</summary>
public sealed record SemanticGraphSlice
{
    /// <summary>Gets the source family base path when family lifecycle notifications are available.</summary>
    public string? InputFamilyBasePath { get; init; }
    /// <summary>Gets the inclusive event-time window start.</summary>
    public required long WindowStartNanos { get; init; }
    /// <summary>Gets the exclusive event-time window end.</summary>
    public required long WindowEndNanos { get; init; }
    /// <summary>Gets finalized behavioral documents.</summary>
    public required IReadOnlyList<BehavioralDocument> Documents { get; init; }
    /// <summary>Gets finalized weighted interactions.</summary>
    public required IReadOnlyList<WeightedInteraction> Interactions { get; init; }
    /// <summary>Gets a cumulative metrics snapshot taken for this emission.</summary>
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
    private long _processingEndedAtTicks; // 0 means null

    /// <summary>Gets the number of graph facts read.</summary>
    public long FactsRead
    {
        get => Interlocked.Read(ref _factsRead);
        internal set => Interlocked.Exchange(ref _factsRead, value);
    }
    /// <summary>Gets the number of edge facts read.</summary>
    public long EdgeFactsRead
    {
        get => Interlocked.Read(ref _edgeFactsRead);
        internal set => Interlocked.Exchange(ref _edgeFactsRead, value);
    }
    /// <summary>Gets the number of attribute facts read.</summary>
    public long AttributeFactsRead
    {
        get => Interlocked.Read(ref _attributeFactsRead);
        internal set => Interlocked.Exchange(ref _attributeFactsRead, value);
    }
    /// <summary>Gets the number of active documents created.</summary>
    public long DocumentsCreated
    {
        get => Interlocked.Read(ref _documentsCreated);
        internal set => Interlocked.Exchange(ref _documentsCreated, value);
    }
    /// <summary>Gets the number of documents emitted in closed slices.</summary>
    public long DocumentsClosed
    {
        get => Interlocked.Read(ref _documentsClosed);
        internal set => Interlocked.Exchange(ref _documentsClosed, value);
    }
    /// <summary>Gets the number of active interactions created.</summary>
    public long InteractionsCreated
    {
        get => Interlocked.Read(ref _interactionsCreated);
        internal set => Interlocked.Exchange(ref _interactionsCreated, value);
    }
    /// <summary>Gets the number of interactions emitted in closed slices.</summary>
    public long InteractionsClosed
    {
        get => Interlocked.Read(ref _interactionsClosed);
        internal set => Interlocked.Exchange(ref _interactionsClosed, value);
    }
    /// <summary>Gets the number of timestamp-less edges skipped.</summary>
    public long FactsSkippedWithoutTimestamp
    {
        get => Interlocked.Read(ref _factsSkippedWithoutTimestamp);
        internal set => Interlocked.Exchange(ref _factsSkippedWithoutTimestamp, value);
    }
    /// <summary>Gets the number of facts skipped because their window was already emitted.</summary>
    public long LateFactsSkipped
    {
        get => Interlocked.Read(ref _lateFactsSkipped);
        internal set => Interlocked.Exchange(ref _lateFactsSkipped, value);
    }
    /// <summary>Gets the maximum event lateness among facts skipped as late.</summary>
    public long LateFactLatenessMaxNanos
    {
        get => Interlocked.Read(ref _lateFactLatenessMaxNanos);
        internal set => Interlocked.Exchange(ref _lateFactLatenessMaxNanos, value);
    }
    /// <summary>Gets the one-minute upper-bound estimate of median skipped-fact lateness.</summary>
    public long LateFactLatenessP50Nanos
    {
        get => Interlocked.Read(ref _lateFactLatenessP50Nanos);
        internal set => Interlocked.Exchange(ref _lateFactLatenessP50Nanos, value);
    }
    /// <summary>Gets the one-minute upper-bound estimate of P95 skipped-fact lateness.</summary>
    public long LateFactLatenessP95Nanos
    {
        get => Interlocked.Read(ref _lateFactLatenessP95Nanos);
        internal set => Interlocked.Exchange(ref _lateFactLatenessP95Nanos, value);
    }
    /// <summary>Gets the one-minute upper-bound estimate of P99 skipped-fact lateness.</summary>
    public long LateFactLatenessP99Nanos
    {
        get => Interlocked.Read(ref _lateFactLatenessP99Nanos);
        internal set => Interlocked.Exchange(ref _lateFactLatenessP99Nanos, value);
    }
    /// <summary>Gets the number of slices emitted.</summary>
    public long SlicesEmitted
    {
        get => Interlocked.Read(ref _slicesEmitted);
        internal set => Interlocked.Exchange(ref _slicesEmitted, value);
    }
    /// <summary>Gets the number of missing endpoint-kind lookups.</summary>
    public long UnknownNodeKindUses
    {
        get => Interlocked.Read(ref _unknownNodeKindUses);
        internal set => Interlocked.Exchange(ref _unknownNodeKindUses, value);
    }
    /// <summary>Gets the UTC time processing started.</summary>
    public DateTimeOffset ProcessingStartedAt
    {
        get => new DateTimeOffset(Interlocked.Read(ref _processingStartedAtTicks), TimeSpan.Zero);
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
        internal set
        {
            Interlocked.Exchange(ref _processingEndedAtTicks, value?.UtcTicks ?? 0);
        }
    }

    /// <summary>Creates a thread-safe point-in-time snapshot of these metrics.</summary>
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
