using Spectre.CdmIngestion;
using Spectre.DisparityFiltering;
using Spectre.InvestigationHost.Store;
using Spectre.SemanticIndexing;
using System.Text.Json;

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

        Assert.Equal(StoreQueryStatus.Found, matching.Status);
        Assert.Single(matching.Value!.Edges);
        Assert.Equal(1, matching.Value.TotalMatchingEdges);
        Assert.Equal(StoreQueryStatus.Found, missing.Status);
        Assert.Empty(missing.Value!.Edges);
        Assert.Equal(0, missing.Value.TotalMatchingEdges);
    }

    [Fact]
    public void PairDetailLookup_ReturnsBreakdownsScoresAndEvidence()
    {
        var slice = CreateSlice();
        var edge = Assert.Single(slice.Interactions);
        var store = new DashboardQueryStore(new EventHub());
        store.AcceptSlice(slice);

        var detail = store.GetInteractionDetail(1, 0, edge.SourceNodeId, edge.TargetNodeId);

        Assert.Equal(StoreQueryStatus.Found, detail.Status);
        Assert.Equal(5, detail.Value!.Count);
        Assert.Equal(3, detail.Value.PredicateCounts["WRITE"]);
        Assert.True(detail.Value.SourceOutgoing.IsSignificant);
        Assert.Single(detail.Value.Evidence);
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

    [Fact]
    public void Projection_ReturnsGoneForSummaryOnlyWindows()
    {
        var store = new DashboardQueryStore(new EventHub()) { MaxDetailedSlices = 0, MaxProjectionSlices = 0 };
        store.AcceptSlice(CreateSlice());

        var result = store.GetProjection(1, 0, new GraphQueryParameters());

        Assert.Equal(StoreQueryStatus.Gone, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Projection_ReturnsNotFoundForUnknownWindows()
    {
        var store = new DashboardQueryStore(new EventHub());
        store.AcceptSlice(CreateSlice());

        var result = store.GetProjection(1, 42, new GraphQueryParameters());

        Assert.Equal(StoreQueryStatus.NotFound, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void DetailLookups_ReturnGoneWhenDetailedSliceWasEvicted()
    {
        var store = new DashboardQueryStore(new EventHub()) { MaxDetailedSlices = 0, MaxProjectionSlices = 1 };
        store.AcceptSlice(CreateSlice());

        var node = store.GetNodeDetail(1, 0, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var interaction = store.GetInteractionDetail(
            1,
            0,
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));

        Assert.Equal(StoreQueryStatus.Gone, node.Status);
        Assert.Equal(StoreQueryStatus.Gone, interaction.Status);
    }

    [Theory]
    [InlineData(-0.1, 250, 200, null, null, "minWeight must be a finite number greater than or equal to 0.")]
    [InlineData(0.0, 1, 200, null, null, "maxNodes must be between 2 and 1000.")]
    [InlineData(0.0, 250, 0, null, null, "maxEdges must be between 1 and 2000.")]
    [InlineData(0.0, 250, 200, "EXEC", null, "Unknown predicate 'EXEC'.")]
    [InlineData(0.0, 250, 200, null, "FILE", "Unknown node kind 'FILE'.")]
    public void GraphQueryValidator_RejectsInvalidParameters(
        double minWeight,
        int maxNodes,
        int maxEdges,
        string? predicate,
        string? nodeKind,
        string expectedError)
    {
        var result = GraphQueryValidator.Validate(
            new GraphQueryParameters(minWeight, maxNodes, maxEdges, predicate, nodeKind),
            new HashSet<string> { "READ", "WRITE" },
            new HashSet<string> { "PROCESS" });

        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void LongAsStringJsonConverter_WritesAndReadsInt64AsString()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new LongAsStringJsonConverter() }
        };
        var dto = new StoreMemoryPressureDto(1, 2, 3, 9_007_199_254_740_993, 4, 5, 6, 7, 8, 9, 10, 11);

        var json = JsonSerializer.Serialize(dto, options);
        var roundTripped = JsonSerializer.Deserialize<StoreMemoryPressureDto>(json, options);

        Assert.Contains("\"estimatedDetailedBytes\":\"9007199254740993\"", json);
        Assert.Equal(9_007_199_254_740_993, roundTripped!.EstimatedDetailedBytes);
    }

    [Fact]
    public async Task EventHub_AssignsMonotonicServerSentEventIds()
    {
        var hub = new EventHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = hub.Subscribe(cts.Token);
        var enumerator = events.GetAsyncEnumerator(cts.Token);

        try
        {
            hub.Publish(new ServerSentEvent("first", "{}"));
            hub.Publish(new ServerSentEvent("second", "{}"));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(1, enumerator.Current.Id);
            Assert.Equal("first", enumerator.Current.EventType);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(2, enumerator.Current.Id);
            Assert.Equal("second", enumerator.Current.EventType);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
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
