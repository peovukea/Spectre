using Spectre.Ingestion;
using Spectre.DisparityFiltering.Sinks;
using Spectre.SemanticIndexing;

namespace Spectre.DisparityFiltering.Tests;

internal sealed class CollectingDisparitySink : IDisparityGraphSliceSink
{
    public List<DisparityGraphSlice> Slices { get; } = [];
    public bool Disposed { get; private set; }

    public void Write(DisparityGraphSlice slice) => Slices.Add(slice);
    public void Dispose() => Disposed = true;
}

internal static class Slice
{
    public static readonly SourceLocation Source = new("segment.bin", 12);

    public static SemanticGraphSlice Create(params WeightedInteraction[] interactions)
    {
        var nodeIds = interactions
            .SelectMany(interaction => new[] { interaction.SourceNodeId, interaction.TargetNodeId })
            .Distinct()
            .ToArray();
        return new SemanticGraphSlice
        {
            InputFamilyBasePath = "family.bin",
            WindowStartNanos = 0,
            WindowEndNanos = 1_000,
            Documents = nodeIds.Select(Document).ToArray(),
            Interactions = interactions,
            Metrics = new SemanticIndexingMetrics()
        };
    }

    public static BehavioralDocument Document(Guid nodeId) => new()
    {
        Key = new DocumentKey(nodeId, 0),
        NodeId = nodeId,
        WindowStartNanos = 0,
        WindowEndNanos = 1_000,
        NodeKind = "PROCESS",
        TermCounts = new Dictionary<string, int>(),
        TfidfWeights = new Dictionary<string, double>()
    };

    public static WeightedInteraction Edge(
        Guid source,
        Guid target,
        double weight,
        string predicate = "READ",
        int count = 1,
        IReadOnlyList<EvidencePointer>? evidence = null) => new()
    {
        SourceNodeId = source,
        TargetNodeId = target,
        WindowStartNanos = 0,
        WindowEndNanos = 1_000,
        Predicate = predicate,
        Count = count,
        SemanticWeight = weight,
        TermCounts = new Dictionary<string, int> { [predicate] = count },
        Evidence = evidence ?? []
    };
}
