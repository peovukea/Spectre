namespace Spectre.DisparityFiltering.Sinks;

/// <summary>
/// Synchronously consolidates and disparity-filters closed semantic graph slices.
/// This type is not thread-safe.
/// </summary>
public sealed class DisparityFilteringSemanticGraphSliceSink : ISemanticGraphSliceSink
{
    private readonly IDisparityGraphSliceSink _output;
    private readonly double _alpha;
    private readonly int _maxEvidence;
    private bool _disposed;

    /// <summary>Initializes a filtering sink that owns the downstream output sink.</summary>
    public DisparityFilteringSemanticGraphSliceSink(
        IDisparityGraphSliceSink output,
        DisparityFilterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new DisparityFilterOptions();

        if (!double.IsFinite(options.Alpha) || options.Alpha <= 0 || options.Alpha > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Alpha must be finite and in the range (0, 1].");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxEvidencePointersPerEdge);

        _output = output;
        _alpha = options.Alpha;
        _maxEvidence = options.MaxEvidencePointersPerEdge;
        Metrics = new DisparityFilteringMetrics { ProcessingStartedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>Gets cumulative metrics for this filtering run.</summary>
    public DisparityFilteringMetrics Metrics { get; }

    /// <inheritdoc />
    public void Write(SemanticGraphSlice slice)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(slice);

        var documents = BuildDocumentIndex(slice);
        var candidates = Consolidate(slice, documents);
        var outgoing = BuildPopulations(candidates, candidate => candidate.SourceNodeId);
        var incoming = BuildPopulations(candidates, candidate => candidate.TargetNodeId);

        var retained = candidates
            .Select(candidate => FinalizeCandidate(candidate, outgoing[candidate.SourceNodeId], incoming[candidate.TargetNodeId]))
            .Where(interaction => interaction.SourceOutgoing.IsSignificant || interaction.TargetIncoming.IsSignificant)
            .OrderBy(interaction => CanonicalGuid(interaction.SourceNodeId), StringComparer.Ordinal)
            .ThenBy(interaction => CanonicalGuid(interaction.TargetNodeId), StringComparer.Ordinal)
            .ToArray();

        var retainedNodeIds = retained
            .SelectMany(interaction => new[] { interaction.SourceNodeId, interaction.TargetNodeId })
            .ToHashSet();
        var retainedDocuments = slice.Documents
            .Where(document => retainedNodeIds.Contains(document.NodeId))
            .OrderBy(document => CanonicalGuid(document.NodeId), StringComparer.Ordinal)
            .ToArray();

        var sourceWeight = SumFinite(candidates.Select(candidate => candidate.SemanticWeight), "Source semantic weight overflowed.");
        var retainedWeight = SumFinite(retained.Select(interaction => interaction.SemanticWeight), "Retained semantic weight overflowed.");
        var reduction = new DisparitySliceReduction
        {
            Alpha = _alpha,
            SourceDocumentCount = slice.Documents.Count,
            SourceInteractionCount = slice.Interactions.Count,
            CandidateEdgeCount = candidates.Count,
            RetainedDocumentCount = retainedDocuments.Length,
            RetainedEdgeCount = retained.Length,
            SourceSemanticWeight = sourceWeight,
            RetainedSemanticWeight = retainedWeight
        };

        var metricsSnapshot = Metrics.Snapshot();
        ApplyReduction(metricsSnapshot, reduction);

        _output.Write(new DisparityGraphSlice
        {
            InputFamilyBasePath = slice.InputFamilyBasePath,
            WindowStartNanos = slice.WindowStartNanos,
            WindowEndNanos = slice.WindowEndNanos,
            Documents = retainedDocuments,
            Interactions = retained,
            IndexingMetrics = slice.Metrics,
            Reduction = reduction,
            Metrics = metricsSnapshot
        });

        ApplyReduction(Metrics, reduction);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output.Dispose();
        Metrics.ProcessingEndedAt = DateTimeOffset.UtcNow;
    }

    private static Dictionary<Guid, BehavioralDocument> BuildDocumentIndex(SemanticGraphSlice slice)
    {
        var documents = new Dictionary<Guid, BehavioralDocument>();
        foreach (var document in slice.Documents)
        {
            if (!documents.TryAdd(document.NodeId, document))
            {
                throw new ArgumentException($"Slice contains duplicate document '{document.NodeId:D}'.", nameof(slice));
            }
        }

        return documents;
    }

    private List<CandidateInteraction> Consolidate(
        SemanticGraphSlice slice,
        IReadOnlyDictionary<Guid, BehavioralDocument> documents)
    {
        var candidates = new Dictionary<PairKey, CandidateInteraction>();
        foreach (var interaction in slice.Interactions)
        {
            ValidateInteraction(interaction, documents);
            var key = new PairKey(interaction.SourceNodeId, interaction.TargetNodeId);
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidate = new CandidateInteraction(
                    interaction.SourceNodeId,
                    interaction.TargetNodeId,
                    slice.WindowStartNanos,
                    slice.WindowEndNanos);
                candidates.Add(key, candidate);
            }

            candidate.Count = checked(candidate.Count + interaction.Count);
            candidate.SemanticWeight = AddFinite(candidate.SemanticWeight, interaction.SemanticWeight, "Consolidated semantic weight overflowed.");
            AddCount(candidate.PredicateCounts, interaction.Predicate, interaction.Count);
            AddWeight(candidate.PredicateWeights, interaction.Predicate, interaction.SemanticWeight);
            foreach (var term in interaction.TermCounts)
            {
                if (term.Value <= 0)
                {
                    throw new ArgumentException($"Interaction term '{term.Key}' must have a positive count.", nameof(slice));
                }

                AddCount(candidate.TermCounts, term.Key, term.Value);
            }

            candidate.Evidence.UnionWith(interaction.Evidence);
        }

        return candidates.Values
            .OrderBy(candidate => CanonicalGuid(candidate.SourceNodeId), StringComparer.Ordinal)
            .ThenBy(candidate => CanonicalGuid(candidate.TargetNodeId), StringComparer.Ordinal)
            .ToList();
    }

    private static void ValidateInteraction(
        WeightedInteraction interaction,
        IReadOnlyDictionary<Guid, BehavioralDocument> documents)
    {
        if (interaction.Count <= 0)
        {
            throw new ArgumentException("Interaction count must be positive.", nameof(interaction));
        }

        if (!double.IsFinite(interaction.SemanticWeight) || interaction.SemanticWeight <= 0)
        {
            throw new ArgumentException("Interaction semantic weight must be finite and positive.", nameof(interaction));
        }

        if (!documents.ContainsKey(interaction.SourceNodeId))
        {
            throw new ArgumentException($"Missing source endpoint document '{interaction.SourceNodeId:D}'.", nameof(interaction));
        }

        if (!documents.ContainsKey(interaction.TargetNodeId))
        {
            throw new ArgumentException($"Missing target endpoint document '{interaction.TargetNodeId:D}'.", nameof(interaction));
        }
    }

    private static Dictionary<Guid, Population> BuildPopulations(
        IEnumerable<CandidateInteraction> candidates,
        Func<CandidateInteraction, Guid> endpoint)
    {
        var populations = new Dictionary<Guid, Population>();
        foreach (var candidate in candidates)
        {
            var nodeId = endpoint(candidate);
            if (!populations.TryGetValue(nodeId, out var population))
            {
                population = new Population();
                populations.Add(nodeId, population);
            }

            population.Degree = checked(population.Degree + 1);
            population.Strength = AddFinite(population.Strength, candidate.SemanticWeight, "Endpoint strength overflowed.");
        }

        return populations;
    }

    private BackboneInteraction FinalizeCandidate(
        CandidateInteraction candidate,
        Population outgoing,
        Population incoming) =>
        new()
        {
            SourceNodeId = candidate.SourceNodeId,
            TargetNodeId = candidate.TargetNodeId,
            WindowStartNanos = candidate.WindowStartNanos,
            WindowEndNanos = candidate.WindowEndNanos,
            Count = candidate.Count,
            SemanticWeight = candidate.SemanticWeight,
            PredicateCounts = Ordered(candidate.PredicateCounts),
            PredicateSemanticWeights = Ordered(candidate.PredicateWeights),
            TermCounts = Ordered(candidate.TermCounts),
            Evidence = candidate.Evidence
                .OrderBy(evidence => evidence.TimestampNanos is null ? 1 : 0)
                .ThenBy(evidence => evidence.TimestampNanos)
                .ThenBy(evidence => evidence.Source.SegmentPath, StringComparer.Ordinal)
                .ThenBy(evidence => evidence.Source.SyncBlockOffset)
                .ThenBy(evidence => evidence.EventId is null ? 1 : 0)
                .ThenBy(evidence => evidence.EventId)
                .Take(_maxEvidence)
                .ToArray(),
            SourceOutgoing = Score(candidate.SemanticWeight, outgoing),
            TargetIncoming = Score(candidate.SemanticWeight, incoming)
        };

    private DirectionalDisparityScore Score(double weight, Population population)
    {
        var normalized = Math.Clamp(weight / population.Strength, 0, 1);
        if (population.Degree == 1)
        {
            return new DirectionalDisparityScore
            {
                Degree = 1,
                Strength = population.Strength,
                NormalizedWeight = normalized,
                Significance = null,
                IsSignificant = false
            };
        }

        var significance = Math.Pow(1 - normalized, population.Degree - 1);
        return new DirectionalDisparityScore
        {
            Degree = population.Degree,
            Strength = population.Strength,
            NormalizedWeight = normalized,
            Significance = significance,
            IsSignificant = significance < _alpha
        };
    }

    private static void AddCount(IDictionary<string, int> values, string key, int increment)
    {
        values.TryGetValue(key, out var current);
        values[key] = checked(current + increment);
    }

    private static void AddWeight(IDictionary<string, double> values, string key, double increment)
    {
        values.TryGetValue(key, out var current);
        values[key] = AddFinite(current, increment, $"Predicate semantic weight '{key}' overflowed.");
    }

    private static double AddFinite(double left, double right, string message)
    {
        var value = left + right;
        return double.IsFinite(value) ? value : throw new OverflowException(message);
    }

    private static double SumFinite(IEnumerable<double> values, string message)
    {
        var total = 0d;
        foreach (var value in values)
        {
            total = AddFinite(total, value, message);
        }

        return total;
    }

    private static void ApplyReduction(DisparityFilteringMetrics metrics, DisparitySliceReduction reduction)
    {
        metrics.SourceDocuments = checked(metrics.SourceDocuments + reduction.SourceDocumentCount);
        metrics.SourceInteractions = checked(metrics.SourceInteractions + reduction.SourceInteractionCount);
        metrics.CandidateEdges = checked(metrics.CandidateEdges + reduction.CandidateEdgeCount);
        metrics.RetainedDocuments = checked(metrics.RetainedDocuments + reduction.RetainedDocumentCount);
        metrics.RetainedEdges = checked(metrics.RetainedEdges + reduction.RetainedEdgeCount);
        metrics.SourceSemanticWeight = AddFinite(
            metrics.SourceSemanticWeight,
            reduction.SourceSemanticWeight,
            "Cumulative source semantic weight overflowed.");
        metrics.RetainedSemanticWeight = AddFinite(
            metrics.RetainedSemanticWeight,
            reduction.RetainedSemanticWeight,
            "Cumulative retained semantic weight overflowed.");
        metrics.SlicesEmitted = checked(metrics.SlicesEmitted + 1);
    }

    private static SortedDictionary<string, TValue> Ordered<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string CanonicalGuid(Guid value) => value.ToString("D");

    private readonly record struct PairKey(Guid SourceNodeId, Guid TargetNodeId);

    private sealed class Population
    {
        public int Degree { get; set; }
        public double Strength { get; set; }
    }

    private sealed class CandidateInteraction(
        Guid sourceNodeId,
        Guid targetNodeId,
        long windowStartNanos,
        long windowEndNanos)
    {
        public Guid SourceNodeId { get; } = sourceNodeId;
        public Guid TargetNodeId { get; } = targetNodeId;
        public long WindowStartNanos { get; } = windowStartNanos;
        public long WindowEndNanos { get; } = windowEndNanos;
        public int Count { get; set; }
        public double SemanticWeight { get; set; }
        public Dictionary<string, int> PredicateCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> PredicateWeights { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TermCounts { get; } = new(StringComparer.Ordinal);
        public HashSet<EvidencePointer> Evidence { get; } = [];
    }
}
