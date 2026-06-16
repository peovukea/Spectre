using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Sinks;
using Spectre.SemanticIndexing.Metadata;
using Spectre.SemanticIndexing.Metrics;
using Spectre.SemanticIndexing.Terms;

namespace Spectre.SemanticIndexing.Sinks;

/// <summary>
/// Synchronously converts graph facts into closed semantic graph slices.
/// This type is not thread-safe.
/// </summary>
public sealed class SemanticIndexingGraphFactSink : IGraphFactFamilySink
{
    private readonly ISemanticGraphSliceSink _output;
    private readonly long _windowSizeNanos;
    private readonly long _allowedLatenessNanos;
    private readonly int _maxEvidence;
    private readonly Dictionary<Guid, NodeMetadata> _metadata = [];
    private readonly SortedDictionary<long, WindowState> _windows = [];
    private readonly Dictionary<string, long> _documentFrequency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _nodeKindBaselines = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> _previousSelf = [];
    private readonly LateFactLatenessHistogram _lateFactLateness = new();
    private long _closedThroughNanos = long.MinValue;
    private long _documentCount;
    private long? _maxSeenTimestampNanos;
    private string? _currentFamilyBasePath;
    private bool _disposed;

    /// <summary>Initializes a semantic indexing graph-fact sink that owns the output sink.</summary>
    public SemanticIndexingGraphFactSink(
        ISemanticGraphSliceSink output,
        SemanticIndexingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new SemanticIndexingOptions();

        if (options.WindowSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WindowSize must be positive.");
        }

        if (options.AllowedLateness < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AllowedLateness must be non-negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxEvidencePointersPerInteraction);

        try
        {
            _windowSizeNanos = checked(options.WindowSize.Ticks * 100);
            _allowedLatenessNanos = checked(options.AllowedLateness.Ticks * 100);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Time spans must fit in nanoseconds.");
        }

        _output = output;
        _maxEvidence = options.MaxEvidencePointersPerInteraction;
        Metrics = new SemanticIndexingMetrics { ProcessingStartedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>Gets cumulative metrics for this indexing run.</summary>
    public SemanticIndexingMetrics Metrics { get; }

    /// <inheritdoc />
    public void BeginFamily(string familyBasePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyBasePath);

        if (_currentFamilyBasePath is not null || _windows.Count != 0)
        {
            throw new InvalidOperationException("A semantic indexing family is already active.");
        }

        _currentFamilyBasePath = familyBasePath;
        ResetFamilyState();
    }

    /// <inheritdoc />
    public void EndFamily(string familyBasePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyBasePath);

        if (!string.Equals(_currentFamilyBasePath, familyBasePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Semantic indexing family '{familyBasePath}' is not active.");
        }

        FlushOpenWindows();
        _currentFamilyBasePath = null;
        ResetFamilyState();
    }

    /// <inheritdoc />
    public void Write(GraphFact fact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fact);
        Metrics.FactsRead++;

        switch (fact)
        {
            case AttributeFact attribute:
                Metrics.AttributeFactsRead++;
                GetMetadata(attribute.SubjectId).Set(attribute.Predicate, attribute.LiteralValue);
                break;
            case EdgeFact edge:
                Metrics.EdgeFactsRead++;
                WriteEdge(edge);
                break;
            default:
                throw new ArgumentException($"Unsupported graph fact type '{fact.GetType().Name}'.", nameof(fact));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? exceptions = null;

        try
        {
            FlushOpenWindows();
        }
        catch (Exception exception)
        {
            (exceptions ??= []).Add(exception);
        }

        try
        {
            _output.Dispose();
        }
        catch (Exception exception)
        {
            (exceptions ??= []).Add(exception);
        }

        if (exceptions is null)
        {
            Metrics.ProcessingEndedAt = DateTimeOffset.UtcNow;
            return;
        }

        if (exceptions.Count == 1)
        {
            throw exceptions[0];
        }

        throw new AggregateException("Semantic indexing disposal failed.", exceptions);
    }

    private void WriteEdge(EdgeFact edge)
    {
        if (edge.TimestampNanos is not { } timestamp)
        {
            Metrics.FactsSkippedWithoutTimestamp++;
            return;
        }

        _maxSeenTimestampNanos = _maxSeenTimestampNanos is { } current
            ? Math.Max(current, timestamp)
            : timestamp;

        var windowStart = GetWindowStart(timestamp);
        var windowEnd = checked(windowStart + _windowSizeNanos);
        if (windowEnd <= _closedThroughNanos)
        {
            Metrics.LateFactsSkipped++;
            _lateFactLateness.Record(GetLatenessNanos(timestamp), Metrics);
            CloseEligibleWindows();
            return;
        }

        if (!_windows.TryGetValue(windowStart, out var window))
        {
            window = new WindowState(windowStart, windowEnd);
            _windows.Add(windowStart, window);
        }

        _metadata.TryGetValue(edge.SubjectId, out var sourceMetadata);
        _metadata.TryGetValue(edge.ObjectId, out var targetMetadata);
        var sourceKind = ResolveNodeKind(sourceMetadata);
        string targetKind;
        if (edge.SubjectId == edge.ObjectId)
        {
            targetKind = sourceKind;
        }
        else
        {
            targetKind = ResolveNodeKind(targetMetadata);
        }

        var outgoing = SemanticTermExtractor.Outgoing(edge.Predicate, targetKind, targetMetadata);
        var incoming = SemanticTermExtractor.Incoming(edge.Predicate, sourceKind, sourceMetadata);

        var sourceDocument = GetDocument(window, edge.SubjectId, sourceMetadata);
        AddTerms(sourceDocument.TermCounts, outgoing);

        if (edge.SubjectId == edge.ObjectId)
        {
            AddTerms(sourceDocument.TermCounts, incoming);
        }
        else
        {
            var targetDocument = GetDocument(window, edge.ObjectId, targetMetadata);
            AddTerms(targetDocument.TermCounts, incoming);
        }

        var key = new InteractionKey(edge.SubjectId, edge.ObjectId, edge.Predicate);
        if (!window.Interactions.TryGetValue(key, out var interaction))
        {
            interaction = new ActiveInteraction();
            window.Interactions.Add(key, interaction);
            Metrics.InteractionsCreated++;
        }

        interaction.Count = checked(interaction.Count + 1);
        AddTerms(interaction.TermCounts, outgoing);
        AddTerms(interaction.TermCounts, incoming);
        if (interaction.Evidence.Count < _maxEvidence)
        {
            interaction.Evidence.Add(new EvidencePointer(edge.Source, edge.TimestampNanos, edge.EventId));
        }

        CloseEligibleWindows();
    }

    private void CloseEligibleWindows()
    {
        if (_maxSeenTimestampNanos is not { } maxSeen)
        {
            return;
        }

        var watermark = maxSeen < long.MinValue + _allowedLatenessNanos
            ? long.MinValue
            : maxSeen - _allowedLatenessNanos;

        foreach (var start in _windows
                     .Where(pair => pair.Value.EndNanos <= watermark)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            EmitWindow(start);
        }
    }

    private void EmitWindow(long windowStart)
    {
        var window = _windows[windowStart];
        var orderedDocuments = window.Documents.Values
            .OrderBy(document => CanonicalGuid(document.NodeId), StringComparer.Ordinal)
            .ToArray();

        var finalized = new List<BehavioralDocument>(orderedDocuments.Length);
        foreach (var document in orderedDocuments)
        {
            var terms = document.TermCounts.Keys.ToHashSet(StringComparer.Ordinal);
            _nodeKindBaselines.TryGetValue(document.NodeKind, out var kindBaseline);
            _previousSelf.TryGetValue(document.NodeId, out var selfBaseline);
            finalized.Add(new BehavioralDocument
            {
                Key = new DocumentKey(document.NodeId, window.StartNanos),
                NodeId = document.NodeId,
                WindowStartNanos = window.StartNanos,
                WindowEndNanos = window.EndNanos,
                NodeKind = document.NodeKind,
                TermCounts = Ordered(document.TermCounts),
                TfidfWeights = new SortedDictionary<string, double>(StringComparer.Ordinal),
                JaccardToNodeKindBaseline = kindBaseline is null ? null : Jaccard(terms, kindBaseline),
                JaccardToPreviousSelf = selfBaseline is null ? null : Jaccard(terms, selfBaseline)
            });
        }

        UpdateBaselines(orderedDocuments);
        UpdateDocumentFrequency(orderedDocuments);

        finalized = finalized.Select(document => document with
        {
            TfidfWeights = Ordered(document.TermCounts.ToDictionary(
                pair => pair.Key,
                pair => Tf(pair.Value) * Idf(pair.Key),
                StringComparer.Ordinal))
        }).ToList();

        var interactions = window.Interactions
            .OrderBy(pair => CanonicalGuid(pair.Key.SourceNodeId), StringComparer.Ordinal)
            .ThenBy(pair => CanonicalGuid(pair.Key.TargetNodeId), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Predicate, StringComparer.Ordinal)
            .Select(pair => FinalizeInteraction(window, pair.Key, pair.Value))
            .ToArray();

        var metricsSnapshot = Metrics.Snapshot();
        metricsSnapshot.DocumentsClosed += finalized.Count;
        metricsSnapshot.InteractionsClosed += interactions.Length;
        metricsSnapshot.SlicesEmitted++;

        _output.Write(new SemanticGraphSlice
        {
            InputFamilyBasePath = _currentFamilyBasePath,
            WindowStartNanos = window.StartNanos,
            WindowEndNanos = window.EndNanos,
            Documents = finalized,
            Interactions = interactions,
            Metrics = metricsSnapshot
        });

        Metrics.DocumentsClosed += finalized.Count;
        Metrics.InteractionsClosed += interactions.Length;
        Metrics.SlicesEmitted++;
        _closedThroughNanos = Math.Max(_closedThroughNanos, window.EndNanos);
        _windows.Remove(windowStart);
    }

    private WeightedInteraction FinalizeInteraction(
        WindowState window,
        InteractionKey key,
        ActiveInteraction interaction)
    {
        var weight = interaction.TermCounts.Count == 0
            ? interaction.Count
            : interaction.TermCounts.Sum(pair => Tf(pair.Value) * Idf(pair.Key));

        if (weight <= 0)
        {
            weight = interaction.Count;
        }

        return new WeightedInteraction
        {
            SourceNodeId = key.SourceNodeId,
            TargetNodeId = key.TargetNodeId,
            WindowStartNanos = window.StartNanos,
            WindowEndNanos = window.EndNanos,
            Predicate = key.Predicate,
            Count = interaction.Count,
            SemanticWeight = weight,
            TermCounts = Ordered(interaction.TermCounts),
            Evidence = interaction.Evidence.ToArray()
        };
    }

    private ActiveDocument GetDocument(WindowState window, Guid nodeId, NodeMetadata? metadata)
    {
        if (window.Documents.TryGetValue(nodeId, out var document))
        {
            return document;
        }

        document = new ActiveDocument(nodeId, ResolveDocumentNodeKind(metadata));
        window.Documents.Add(nodeId, document);
        Metrics.DocumentsCreated++;
        return document;
    }

    private string ResolveNodeKind(NodeMetadata? metadata)
    {
        var value = metadata?.Resolve("HAS_NODE_KIND");
        if (string.IsNullOrWhiteSpace(value))
        {
            Metrics.UnknownNodeKindUses++;
            return "UNKNOWN";
        }

        return value.Trim().ToUpperInvariant();
    }

    private static string ResolveDocumentNodeKind(NodeMetadata? metadata)
    {
        var value = metadata?.Resolve("HAS_NODE_KIND");
        return string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim().ToUpperInvariant();
    }

    private void UpdateBaselines(IEnumerable<ActiveDocument> documents)
    {
        foreach (var document in documents)
        {
            var terms = document.TermCounts.Keys.ToHashSet(StringComparer.Ordinal);
            if (!_nodeKindBaselines.TryGetValue(document.NodeKind, out var kindBaseline))
            {
                kindBaseline = new HashSet<string>(StringComparer.Ordinal);
                _nodeKindBaselines.Add(document.NodeKind, kindBaseline);
            }

            kindBaseline.UnionWith(terms);
            _previousSelf[document.NodeId] = terms;
        }
    }

    private void UpdateDocumentFrequency(IEnumerable<ActiveDocument> documents)
    {
        foreach (var document in documents)
        {
            _documentCount++;
            foreach (var term in document.TermCounts.Keys)
            {
                _documentFrequency.TryGetValue(term, out var count);
                _documentFrequency[term] = count + 1;
            }
        }
    }

    private double Idf(string term)
    {
        _documentFrequency.TryGetValue(term, out var frequency);
        return Math.Log((_documentCount + 1d) / (frequency + 1d)) + 1d;
    }

    private long GetWindowStart(long timestamp)
    {
        var remainder = timestamp % _windowSizeNanos;
        return remainder < 0
            ? checked(timestamp - remainder - _windowSizeNanos)
            : timestamp - remainder;
    }

    private void FlushOpenWindows()
    {
        foreach (var start in _windows.Keys.ToArray())
        {
            EmitWindow(start);
        }
    }

    private void ResetFamilyState()
    {
        _metadata.Clear();
        _documentFrequency.Clear();
        _nodeKindBaselines.Clear();
        _previousSelf.Clear();
        _documentCount = 0;
        _maxSeenTimestampNanos = null;
        _closedThroughNanos = long.MinValue;
    }

    private long GetLatenessNanos(long timestamp)
    {
        var maxSeen = _maxSeenTimestampNanos.GetValueOrDefault();
        var watermark = maxSeen < long.MinValue + _allowedLatenessNanos
            ? long.MinValue
            : maxSeen - _allowedLatenessNanos;

        if (watermark <= timestamp)
        {
            return 0;
        }

        return timestamp < 0 && watermark > long.MaxValue + timestamp
            ? long.MaxValue
            : watermark - timestamp;
    }

    private NodeMetadata GetMetadata(Guid nodeId)
    {
        if (!_metadata.TryGetValue(nodeId, out var metadata))
        {
            metadata = new NodeMetadata();
            _metadata.Add(nodeId, metadata);
        }

        return metadata;
    }

    private static void AddTerms(IDictionary<string, int> counts, IEnumerable<string> terms)
    {
        foreach (var term in terms)
        {
            counts.TryGetValue(term, out var count);
            counts[term] = checked(count + 1);
        }
    }

    private static double Tf(int count) => 1d + Math.Log(count);

    private static double Jaccard(ISet<string> left, ISet<string> right)
    {
        if (left.Count == 0 && right.Count == 0)
        {
            return 1d;
        }

        var intersection = left.Count(term => right.Contains(term));
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    private static SortedDictionary<string, TValue> Ordered<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string CanonicalGuid(Guid value) => value.ToString("D");

    private sealed class WindowState(long startNanos, long endNanos)
    {
        public long StartNanos { get; } = startNanos;
        public long EndNanos { get; } = endNanos;
        public Dictionary<Guid, ActiveDocument> Documents { get; } = [];
        public Dictionary<InteractionKey, ActiveInteraction> Interactions { get; } = [];
    }

    private sealed class ActiveDocument(Guid nodeId, string nodeKind)
    {
        public Guid NodeId { get; } = nodeId;
        public string NodeKind { get; } = nodeKind;
        public Dictionary<string, int> TermCounts { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ActiveInteraction
    {
        public int Count { get; set; }
        public Dictionary<string, int> TermCounts { get; } = new(StringComparer.Ordinal);
        public List<EvidencePointer> Evidence { get; } = [];
    }

    private readonly record struct InteractionKey(Guid SourceNodeId, Guid TargetNodeId, string Predicate);
}
