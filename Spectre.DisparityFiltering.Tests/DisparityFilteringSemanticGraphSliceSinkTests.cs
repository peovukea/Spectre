using Spectre.DisparityFiltering.Sinks;
using Spectre.SemanticIndexing;

namespace Spectre.DisparityFiltering.Tests;

public sealed class DisparityFilteringSemanticGraphSliceSinkTests
{
    [Fact]
    public void ExactDistribution_RetainsOnlyDisproportionateEdge()
    {
        var source = Guid.NewGuid();
        var targets = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output, alpha: 0.05))
        {
            sink.Write(Slice.Create(
                Slice.Edge(source, targets[0], 0.8),
                Slice.Edge(source, targets[1], 0.1),
                Slice.Edge(source, targets[2], 0.1)));
        }

        var edge = Assert.Single(output.Slices.Single().Interactions);
        Assert.Equal(targets[0], edge.TargetNodeId);
        Assert.Equal(0.04, edge.SourceOutgoing.Significance!.Value, 10);
        Assert.True(edge.SourceOutgoing.IsSignificant);
        Assert.False(edge.TargetIncoming.IsSignificant);
    }

    [Fact]
    public void StrictThreshold_DoesNotRetainEqualSignificance()
    {
        var source = Guid.NewGuid();
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output, alpha: 0.25))
        {
            sink.Write(Slice.Create(
                Slice.Edge(source, Guid.NewGuid(), 3),
                Slice.Edge(source, Guid.NewGuid(), 1)));
        }

        Assert.Empty(output.Slices.Single().Interactions);
    }

    [Fact]
    public void DirectedOrRule_RetainsWhenOnlyIncomingDirectionPasses()
    {
        var target = Guid.NewGuid();
        var source = Guid.NewGuid();
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output))
        {
            sink.Write(Slice.Create(
                Slice.Edge(source, target, 0.8),
                Slice.Edge(Guid.NewGuid(), target, 0.1),
                Slice.Edge(Guid.NewGuid(), target, 0.1)));
        }

        var edge = Assert.Single(output.Slices.Single().Interactions);
        Assert.False(edge.SourceOutgoing.IsSignificant);
        Assert.True(edge.TargetIncoming.IsSignificant);
    }

    [Fact]
    public void DegreeOneDirections_NeverAutomaticallyPass()
    {
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output))
        {
            sink.Write(Slice.Create(Slice.Edge(Guid.NewGuid(), Guid.NewGuid(), 10)));
        }

        var slice = output.Slices.Single();
        Assert.Empty(slice.Interactions);
        Assert.Empty(slice.Documents);
        Assert.Equal(1, slice.Reduction.CandidateEdgeCount);
        Assert.Equal(0, slice.Reduction.RetainedEdgeCount);
    }

    [Fact]
    public void ParallelPredicates_CollapseAndUseSummedSemanticWeight()
    {
        var source = Guid.NewGuid();
        var retainedTarget = Guid.NewGuid();
        var evidence = new EvidencePointer(Slice.Source, 5, Guid.NewGuid());
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output, evidenceCap: 2))
        {
            sink.Write(Slice.Create(
                Slice.Edge(source, retainedTarget, 0.4, "READ", 2, [evidence]),
                Slice.Edge(source, retainedTarget, 0.4, "WRITE", 3, [evidence]),
                Slice.Edge(source, Guid.NewGuid(), 0.1),
                Slice.Edge(source, Guid.NewGuid(), 0.1)));
        }

        var edge = Assert.Single(output.Slices.Single().Interactions);
        Assert.Equal(5, edge.Count);
        Assert.Equal(0.8, edge.SemanticWeight, 10);
        Assert.Equal(2, edge.PredicateCounts["READ"]);
        Assert.Equal(3, edge.PredicateCounts["WRITE"]);
        Assert.Single(edge.Evidence);
    }

    [Fact]
    public void SelfLoop_ParticipatesInOutgoingAndIncomingPopulations()
    {
        var node = Guid.NewGuid();
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output))
        {
            sink.Write(Slice.Create(
                Slice.Edge(node, node, 0.96),
                Slice.Edge(node, Guid.NewGuid(), 0.04),
                Slice.Edge(Guid.NewGuid(), node, 0.04)));
        }

        var edge = output.Slices.Single().Interactions.Single(interaction => interaction.SourceNodeId == node && interaction.TargetNodeId == node);
        Assert.Equal(2, edge.SourceOutgoing.Degree);
        Assert.Equal(2, edge.TargetIncoming.Degree);
    }

    [Fact]
    public void Evidence_IsDeduplicatedOrderedAndCapped()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var first = new EvidencePointer(new("a.bin", 1), 10, Guid.Empty);
        var second = new EvidencePointer(new("b.bin", 1), 20, Guid.Empty);
        var last = new EvidencePointer(new("c.bin", 1), null, null);
        var output = new CollectingDisparitySink();
        using (var sink = Filter(output, evidenceCap: 2))
        {
            sink.Write(Slice.Create(
                Slice.Edge(source, target, 0.8, "A", evidence: [last, second, first]),
                Slice.Edge(source, target, 0.1, "B", evidence: [first]),
                Slice.Edge(source, Guid.NewGuid(), 0.05),
                Slice.Edge(source, Guid.NewGuid(), 0.05)));
        }

        Assert.Equal([first, second], output.Slices.Single().Interactions.Single().Evidence);
    }

    [Fact]
    public void MetricsSnapshotsAndDisposeOwnership_ArePreserved()
    {
        var output = new CollectingDisparitySink();
        var sink = Filter(output);
        sink.Write(Slice.Create(Slice.Edge(Guid.NewGuid(), Guid.NewGuid(), 1)));
        sink.Dispose();

        var metrics = output.Slices.Single().Metrics;
        Assert.Equal(1, metrics.SourceInteractions);
        Assert.Equal(1, metrics.CandidateEdges);
        Assert.Equal(1, metrics.SlicesEmitted);
        Assert.NotNull(sink.Metrics.ProcessingEndedAt);
        Assert.True(output.Disposed);
        Assert.Throws<ObjectDisposedException>(() => sink.Write(Slice.Create()));
    }

    [Fact]
    public void InvalidOptionsAndInteractions_FailFast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Filter(new CollectingDisparitySink(), alpha: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Filter(new CollectingDisparitySink(), evidenceCap: -1));

        var output = new CollectingDisparitySink();
        using var sink = Filter(output);
        Assert.Throws<ArgumentException>(() => sink.Write(Slice.Create(Slice.Edge(Guid.NewGuid(), Guid.NewGuid(), double.NaN))));
    }

    [Fact]
    public void MissingEndpointDocument_FailsFast()
    {
        var interaction = Slice.Edge(Guid.NewGuid(), Guid.NewGuid(), 1);
        var slice = Slice.Create(interaction) with { Documents = [Slice.Document(interaction.SourceNodeId)] };
        using var sink = Filter(new CollectingDisparitySink());

        Assert.Throws<ArgumentException>(() => sink.Write(slice));
    }

    private static DisparityFilteringSemanticGraphSliceSink Filter(
        CollectingDisparitySink output,
        double alpha = 0.05,
        int evidenceCap = 3) =>
        new(output, new DisparityFilterOptions
        {
            Alpha = alpha,
            MaxEvidencePointersPerEdge = evidenceCap
        });
}
