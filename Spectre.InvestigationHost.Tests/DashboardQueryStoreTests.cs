using Spectre.CdmIngestion;
using Spectre.DisparityFiltering;
using Spectre.InvestigationHost.Store;
using Spectre.SemanticIndexing;

namespace Spectre.InvestigationHost.Tests;

public sealed class DashboardQueryStoreTests
{
    [Fact]
    public void SummaryAndPredicates_ReflectRetainedPairEdgeBreakdown()
    {
        var store = new DashboardQueryStore(new EventHub());
        store.AcceptSlice(CreateSlice());

        var summary = Assert.Single(store.GetWindows(1));
        Assert.Equal(1, summary.InteractionCount);
        Assert.Equal(2, summary.PredicateCounts["READ"]);
        Assert.Equal(3, summary.PredicateCounts["WRITE"]);
        Assert.Equal(4, summary.Reduction.SourceInteractionCount);
        Assert.Equal(["READ", "WRITE"], store.GetPredicates().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProjectionPredicateFilter_MatchesMembershipInsidePairEdge()
    {
        var store = new DashboardQueryStore(new EventHub());
        store.AcceptSlice(CreateSlice());

        var matching = store.GetProjection(1, 0, new GraphQueryParameters(Predicate: "WRITE"));
        var missing = store.GetProjection(1, 0, new GraphQueryParameters(Predicate: "EXEC"));

        Assert.Single(matching!.Edges);
        Assert.Equal(1, matching.TotalMatchingEdges);
        Assert.Empty(missing!.Edges);
        Assert.Equal(0, missing.TotalMatchingEdges);
    }

    [Fact]
    public void PairDetailLookup_ReturnsBreakdownsScoresAndEvidence()
    {
        var slice = CreateSlice();
        var edge = Assert.Single(slice.Interactions);
        var store = new DashboardQueryStore(new EventHub());
        store.AcceptSlice(slice);

        var detail = store.GetInteractionDetail(1, 0, edge.SourceNodeId, edge.TargetNodeId);

        Assert.NotNull(detail);
        Assert.Equal(5, detail.Count);
        Assert.Equal(3, detail.PredicateCounts["WRITE"]);
        Assert.True(detail.SourceOutgoing.IsSignificant);
        Assert.Single(detail.Evidence);
    }

    [Fact]
    public void RunStatus_ExposesIndexingAndFilteringMetrics()
    {
        var slice = CreateSlice();
        var store = new DashboardQueryStore(new EventHub());
        store.AcceptSlice(slice);
        store.MarkRunState(RunState.Completed);

        var status = store.GetRunStatus();
        Assert.NotNull(status.IndexingMetrics);
        Assert.NotNull(status.FilteringMetrics);
    }

    private static DisparityGraphSlice CreateSlice()
    {
        var source = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var target = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var score = new DirectionalDisparityScore
        {
            Degree = 3,
            Strength = 10,
            NormalizedWeight = 0.8,
            Significance = 0.04,
            IsSignificant = true
        };

        return new DisparityGraphSlice
        {
            InputFamilyBasePath = "family.bin",
            WindowStartNanos = 0,
            WindowEndNanos = 1_000,
            Documents = [Document(source), Document(target)],
            Interactions =
            [
                new BackboneInteraction
                {
                    SourceNodeId = source,
                    TargetNodeId = target,
                    WindowStartNanos = 0,
                    WindowEndNanos = 1_000,
                    Count = 5,
                    SemanticWeight = 8,
                    PredicateCounts = new Dictionary<string, int> { ["READ"] = 2, ["WRITE"] = 3 },
                    PredicateSemanticWeights = new Dictionary<string, double> { ["READ"] = 3, ["WRITE"] = 5 },
                    TermCounts = new Dictionary<string, int> { ["TERM"] = 5 },
                    Evidence = [new EvidencePointer(new SourceLocation("segment.bin", 12), 100, Guid.Empty)],
                    SourceOutgoing = score,
                    TargetIncoming = score with { IsSignificant = false }
                }
            ],
            IndexingMetrics = new SemanticIndexingMetrics(),
            Reduction = new DisparitySliceReduction
            {
                Alpha = 0.05,
                SourceDocumentCount = 4,
                SourceInteractionCount = 4,
                CandidateEdgeCount = 3,
                RetainedDocumentCount = 2,
                RetainedEdgeCount = 1,
                SourceSemanticWeight = 10,
                RetainedSemanticWeight = 8
            },
            Metrics = new DisparityFilteringMetrics()
        };
    }

    private static BehavioralDocument Document(Guid nodeId) => new()
    {
        Key = new DocumentKey(nodeId, 0),
        NodeId = nodeId,
        WindowStartNanos = 0,
        WindowEndNanos = 1_000,
        NodeKind = "PROCESS",
        TermCounts = new Dictionary<string, int>(),
        TfidfWeights = new Dictionary<string, double>()
    };
}
