using Spectre.CdmIngestion.Sinks;
using Spectre.SemanticIndexing.Sinks;

namespace Spectre.SemanticIndexing.Tests;

public sealed class SemanticIndexingGraphFactSinkTests
{
    [Fact]
    public void MetadataAliasesAndPrecedence_CreateDirectionalBucketTerms()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var output = new CollectingSliceSink();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Attribute(source, "has_node_kind", "subject_process"));
            sink.Write(Fact.Attribute(target, "HAS_NODE_KIND", "file"));
            sink.Write(Fact.Attribute(target, "HAS_BASE_PROPERTY_PATH", "/tmp/base"));
            sink.Write(Fact.Attribute(target, "HAS_PROPERTY_PATH", "/etc/property"));
            sink.Write(Fact.Attribute(target, "HAS_PATH", "/usr/exact"));
            sink.Write(Fact.Attribute(target, "HAS_PROPERTY_REMOTE_PORT", "443"));
            sink.Write(Fact.Attribute(target, "HAS_REMOTE_IP", "10.2.3.4"));
            sink.Write(Fact.Edge(source, target, 100));
        }

        var outgoing = Assert.Single(output.Slices).Documents.Single(document => document.NodeId == source);
        Assert.Contains("OUT:EVENT_READ:TO_KIND:FILE", outgoing.TermCounts.Keys);
        Assert.Contains("OUT:EVENT_READ:PATH_BUCKET:/USR/*", outgoing.TermCounts.Keys);
        Assert.Contains("OUT:EVENT_READ:REMOTE_PORT:PORT:443", outgoing.TermCounts.Keys);
        Assert.Contains("OUT:EVENT_READ:REMOTE_IP_SCOPE:IP:PRIVATE", outgoing.TermCounts.Keys);
        Assert.DoesNotContain(outgoing.TermCounts.Keys, term => term.Contains("/ETC/*", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAndInvalidOptionalMetadata_AreDistinguished()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var output = new CollectingSliceSink();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Attribute(target, "HAS_REMOTE_PORT", "invalid"));
            sink.Write(Fact.Attribute(target, "HAS_REMOTE_ADDRESS", "invalid"));
            sink.Write(Fact.Attribute(target, "HAS_PATH", " "));
            sink.Write(Fact.Edge(source, target, 100));
        }

        var outgoing = output.Slices.Single().Documents.Single(document => document.NodeId == source);
        Assert.Contains("OUT:EVENT_READ:REMOTE_PORT:PORT:UNKNOWN", outgoing.TermCounts.Keys);
        Assert.Contains("OUT:EVENT_READ:REMOTE_IP_SCOPE:IP:UNKNOWN", outgoing.TermCounts.Keys);
        Assert.Contains("OUT:EVENT_READ:PATH_BUCKET:UNKNOWN", outgoing.TermCounts.Keys);

        var incoming = output.Slices.Single().Documents.Single(document => document.NodeId == target);
        Assert.DoesNotContain(incoming.TermCounts.Keys, term => term.Contains("REMOTE_PORT", StringComparison.Ordinal));
        Assert.DoesNotContain(incoming.TermCounts.Keys, term => term.Contains("REMOTE_IP_SCOPE", StringComparison.Ordinal));
        Assert.DoesNotContain(incoming.TermCounts.Keys, term => term.Contains("PATH_BUCKET", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("22", "PORT:22")]
    [InlineData("53", "PORT:53")]
    [InlineData("80", "PORT:80")]
    [InlineData("443", "PORT:443")]
    [InlineData("0", "PORT:0-1023")]
    [InlineData("1024", "PORT:1024-49151")]
    [InlineData("49152", "PORT:49152-65535")]
    [InlineData("65536", "PORT:UNKNOWN")]
    public void PortBuckets_AreDeterministic(string port, string expected)
    {
        AssertOptionalOutgoingBucket("HAS_REMOTE_PORT", port, $"REMOTE_PORT:{expected}");
    }

    [Theory]
    [InlineData("127.0.0.1", "IP:LOOPBACK")]
    [InlineData("::1", "IP:LOOPBACK")]
    [InlineData("172.31.2.3", "IP:PRIVATE")]
    [InlineData("8.8.8.8", "IP:PUBLIC")]
    [InlineData("bad", "IP:UNKNOWN")]
    public void IpBuckets_AreDeterministic(string address, string expected)
    {
        AssertOptionalOutgoingBucket("HAS_REMOTE_IP", address, $"REMOTE_IP_SCOPE:{expected}");
    }

    [Fact]
    public void UnknownNodeKindUses_CountsDistinctMissingEndpointsIncludingSelfEdge()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var known = Guid.NewGuid();
        var output = new CollectingSliceSink();
        using var sink = Fact.Indexer(output);
        sink.Write(Fact.Attribute(known, "HAS_NODE_KIND", "KNOWN"));
        sink.Write(Fact.Edge(first, second, 100));
        sink.Write(Fact.Edge(first, known, 200));
        sink.Write(Fact.Edge(first, first, 300));

        Assert.Equal(4, sink.Metrics.UnknownNodeKindUses);
    }

    [Fact]
    public void TimestampLessEdge_CreatesNoWindowStateOrScores()
    {
        var output = new CollectingSliceSink();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Edge(Guid.NewGuid(), Guid.NewGuid(), null));
            Assert.Equal(1, sink.Metrics.FactsSkippedWithoutTimestamp);
            Assert.Equal(0, sink.Metrics.DocumentsCreated);
            Assert.Equal(0, sink.Metrics.InteractionsCreated);
        }

        Assert.Empty(output.Slices);
    }

    [Fact]
    public void WatermarkClosesEligibleWindowsAndSkipsOnlyAlreadyEmittedWindows()
    {
        var output = new CollectingSliceSink();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        using (var sink = Fact.Indexer(output, latenessNanos: 1_000))
        {
            sink.Write(Fact.Edge(source, target, 100));
            sink.Write(Fact.Edge(source, target, 1_100));
            Assert.Empty(output.Slices);

            sink.Write(Fact.Edge(source, target, 2_000));
            Assert.Single(output.Slices);

            sink.Write(Fact.Edge(source, target, 900));
            Assert.Equal(1, sink.Metrics.LateFactsSkipped);
            Assert.Equal(100, sink.Metrics.LateFactLatenessMaxNanos);
            Assert.Equal(60_000_000_000, sink.Metrics.LateFactLatenessP50Nanos);
        }

        Assert.Equal([0L, 1_000L, 2_000L], output.Slices.Select(slice => slice.WindowStartNanos));
    }

    [Fact]
    public void FamilyAwareWatermark_AcceptsOlderEventsFromLaterFamilies()
    {
        var output = new CollectingSliceSink();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        using var sink = Fact.Indexer(output);

        sink.BeginFamily("later.bin");
        sink.Write(Fact.Edge(source, target, 10_100));
        sink.EndFamily("later.bin");

        sink.BeginFamily("earlier.bin");
        sink.Write(Fact.Edge(source, target, 100));
        sink.EndFamily("earlier.bin");

        Assert.Equal(0, sink.Metrics.LateFactsSkipped);
        Assert.Equal(["later.bin", "earlier.bin"], output.Slices.Select(slice => slice.InputFamilyBasePath));
        Assert.Equal([10_000L, 0L], output.Slices.Select(slice => slice.WindowStartNanos));
    }

    [Fact]
    public void FamilyAwareWatermark_StillSkipsAlreadyEmittedWindowsWithinFamily()
    {
        var output = new CollectingSliceSink();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        using var sink = Fact.Indexer(output);

        sink.BeginFamily("family.bin");
        sink.Write(Fact.Edge(source, target, 100));
        sink.Write(Fact.Edge(source, target, 1_100));
        sink.Write(Fact.Edge(source, target, 200));
        sink.EndFamily("family.bin");

        Assert.Equal(1, sink.Metrics.LateFactsSkipped);
    }

    [Fact]
    public void FamilyLifecycle_RequiresMatchingNonOverlappingFamilies()
    {
        var output = new CollectingSliceSink();
        using var sink = Fact.Indexer(output);

        sink.BeginFamily("family.bin");

        Assert.Throws<InvalidOperationException>(() => sink.BeginFamily("other.bin"));
        Assert.Throws<InvalidOperationException>(() => sink.EndFamily("other.bin"));
        sink.EndFamily("family.bin");
    }

    [Fact]
    public void LateFactLatenessMetrics_ReportExactMaxAndMinuteBoundedPercentiles()
    {
        var output = new CollectingSliceSink();
        using var sink = Fact.Indexer(output, latenessNanos: 0);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        sink.Write(Fact.Edge(source, target, 3_600_000_000_000));
        sink.Write(Fact.Edge(source, target, 3_600_000_001_000));

        sink.Write(Fact.Edge(source, target, 3_599_999_999_999));
        sink.Write(Fact.Edge(source, target, 3_540_000_001_000));
        sink.Write(Fact.Edge(source, target, 3_480_000_001_000));

        Assert.Equal(3, sink.Metrics.LateFactsSkipped);
        Assert.Equal(120_000_000_000, sink.Metrics.LateFactLatenessMaxNanos);
        Assert.Equal(60_000_000_000, sink.Metrics.LateFactLatenessP50Nanos);
        Assert.Equal(120_000_000_000, sink.Metrics.LateFactLatenessP95Nanos);
        Assert.Equal(120_000_000_000, sink.Metrics.LateFactLatenessP99Nanos);
    }

    [Fact]
    public void InteractionAggregation_CapsEvidenceAndProducesPositiveWeight()
    {
        var output = new CollectingSliceSink();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        using (var sink = Fact.Indexer(output, evidenceCap: 1))
        {
            sink.Write(Fact.Edge(source, target, 100, eventId: Guid.NewGuid()));
            sink.Write(Fact.Edge(source, target, 200, eventId: Guid.NewGuid()));
        }

        var interaction = Assert.Single(Assert.Single(output.Slices).Interactions);
        Assert.Equal(2, interaction.Count);
        Assert.Single(interaction.Evidence);
        Assert.True(interaction.SemanticWeight > 0);
        Assert.Equal(2, interaction.TermCounts["OUT:EVENT_READ"]);
    }

    [Fact]
    public void ZeroEvidenceCap_StoresNoEvidence()
    {
        var output = new CollectingSliceSink();
        using (var sink = Fact.Indexer(output, evidenceCap: 0))
        {
            sink.Write(Fact.Edge(Guid.NewGuid(), Guid.NewGuid(), 100));
        }

        Assert.Empty(output.Slices.Single().Interactions.Single().Evidence);
    }

    [Fact]
    public void PostWindowIdfAndTf_UseExactNaturalLogFormulas()
    {
        var output = new CollectingSliceSink();
        var source = Guid.NewGuid();
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Edge(source, firstTarget, 100));
            sink.Write(Fact.Edge(source, secondTarget, 200));
        }

        var slice = output.Slices.Single();
        var sourceDocument = slice.Documents.Single(document => document.NodeId == source);
        var targetDocument = slice.Documents.Single(document => document.NodeId == firstTarget);
        var commonOutgoing = "OUT:EVENT_READ";
        var commonIncoming = "IN:EVENT_READ";
        var expectedOutgoing = (1 + Math.Log(2)) * (Math.Log(4d / 2d) + 1);
        var expectedIncoming = Math.Log(4d / 3d) + 1;

        Assert.Equal(expectedOutgoing, sourceDocument.TfidfWeights[commonOutgoing], 10);
        Assert.Equal(expectedIncoming, targetDocument.TfidfWeights[commonIncoming], 10);
    }

    [Fact]
    public void JaccardUsesPreWindowBaselinesAndLateMetadataDoesNotRepairOldDocuments()
    {
        var output = new CollectingSliceSink();
        var node = Guid.NewGuid();
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Edge(node, firstTarget, 100, "EVENT_READ"));
            sink.Write(Fact.Edge(Guid.NewGuid(), Guid.NewGuid(), 1_000));
            sink.Write(Fact.Attribute(node, "HAS_NODE_KIND", "PROCESS"));
            sink.Write(Fact.Edge(node, secondTarget, 1_100, "EVENT_READ"));
        }

        var first = output.Slices[0].Documents.Single(document => document.NodeId == node);
        var second = output.Slices[1].Documents.Single(document => document.NodeId == node);
        Assert.Equal("UNKNOWN", first.NodeKind);
        Assert.Equal("PROCESS", second.NodeKind);
        Assert.Equal(1d, second.JaccardToPreviousSelf);
        Assert.Null(second.JaccardToNodeKindBaseline);
    }

    [Fact]
    public void OutputOrderingAndDisposeContract_AreDeterministic()
    {
        var output = new CollectingSliceSink();
        var high = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        IGraphFactSink sink = Fact.Indexer(output);
        sink.Write(Fact.Edge(low, high, 100, "B"));
        sink.Write(Fact.Edge(low, high, 100, "A"));
        sink.Write(Fact.Edge(high, low, 1_100, "Z"));
        sink.Dispose();

        Assert.True(output.Disposed);
        Assert.Equal([0L, 1_000L], output.Slices.Select(slice => slice.WindowStartNanos));
        Assert.Equal(["A", "B"], output.Slices[0].Interactions.Select(interaction => interaction.Predicate));
        Assert.Equal(
            output.Slices[0].Documents.Select(document => document.NodeId.ToString("D")).Order(StringComparer.Ordinal),
            output.Slices[0].Documents.Select(document => document.NodeId.ToString("D")));
        Assert.Throws<ObjectDisposedException>(() => sink.Write(Fact.Edge(low, high, 2_000)));
    }

    [Fact]
    public void SameWindowDocumentsUsePreWindowNodeKindBaseline()
    {
        var output = new CollectingSliceSink();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Attribute(first, "HAS_NODE_KIND", "PROCESS"));
            sink.Write(Fact.Attribute(second, "HAS_NODE_KIND", "PROCESS"));
            sink.Write(Fact.Edge(first, Guid.NewGuid(), 100, "EVENT_READ"));
            sink.Write(Fact.Edge(second, Guid.NewGuid(), 100, "EVENT_WRITE"));
        }

        var documents = output.Slices.Single().Documents
            .Where(document => document.NodeId == first || document.NodeId == second)
            .ToArray();
        Assert.All(documents, document => Assert.Null(document.JaccardToNodeKindBaseline));
    }

    [Fact]
    public void NodeKindBaselineJaccard_IsExactAcrossClosedWindows()
    {
        var output = new CollectingSliceSink();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Attribute(first, "HAS_NODE_KIND", "PROCESS"));
            sink.Write(Fact.Attribute(second, "HAS_NODE_KIND", "PROCESS"));
            sink.Write(Fact.Edge(first, Guid.NewGuid(), 100, "EVENT_READ"));
            sink.Write(Fact.Edge(Guid.NewGuid(), Guid.NewGuid(), 1_000, "EVENT_OTHER"));
            sink.Write(Fact.Edge(second, Guid.NewGuid(), 1_100, "EVENT_READ"));
        }

        var document = output.Slices[1].Documents.Single(item => item.NodeId == second);
        Assert.Equal(1d, document.JaccardToNodeKindBaseline);
    }

    [Fact]
    public void IndexerDoesNotRetainRawFactsOrAdjacencyCollections()
    {
        var fieldTypes = typeof(SemanticIndexingGraphFactSink)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(fieldTypes, type => typeof(Spectre.CdmIngestion.GraphFact).IsAssignableFrom(type));
        Assert.DoesNotContain(fieldTypes, type =>
            type.IsGenericType &&
            type.GetGenericArguments().Any(argument =>
                typeof(Spectre.CdmIngestion.GraphFact).IsAssignableFrom(argument)));
    }

    [Fact]
    public void OptionsValidateWindowLatenessAndEvidenceCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticIndexingGraphFactSink(
            new NullSemanticGraphSliceSink(),
            new SemanticIndexingOptions { WindowSize = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticIndexingGraphFactSink(
            new NullSemanticGraphSliceSink(),
            new SemanticIndexingOptions { AllowedLateness = TimeSpan.FromTicks(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticIndexingGraphFactSink(
            new NullSemanticGraphSliceSink(),
            new SemanticIndexingOptions { MaxEvidencePointersPerInteraction = -1 }));
    }

    [Fact]
    public void MetricsTrackFactVariantsClosureAndSuccessfulCompletion()
    {
        var output = new CollectingSliceSink();
        var sink = Fact.Indexer(output);
        sink.Write(Fact.Attribute(Guid.NewGuid(), "HAS_NAME", "name"));
        sink.Write(Fact.Edge(Guid.NewGuid(), Guid.NewGuid(), 100));
        sink.Dispose();

        Assert.Equal(2, sink.Metrics.FactsRead);
        Assert.Equal(1, sink.Metrics.AttributeFactsRead);
        Assert.Equal(1, sink.Metrics.EdgeFactsRead);
        Assert.Equal(2, sink.Metrics.DocumentsClosed);
        Assert.Equal(1, sink.Metrics.InteractionsClosed);
        Assert.Equal(1, sink.Metrics.SlicesEmitted);
        Assert.Equal(0, sink.Metrics.LateFactLatenessMaxNanos);
        Assert.NotNull(sink.Metrics.ProcessingEndedAt);
    }

    private static void AssertOptionalOutgoingBucket(string predicate, string value, string expectedSuffix)
    {
        var output = new CollectingSliceSink();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        using (var sink = Fact.Indexer(output))
        {
            sink.Write(Fact.Attribute(target, predicate, value));
            sink.Write(Fact.Edge(source, target, 100));
        }

        var document = output.Slices.Single().Documents.Single(item => item.NodeId == source);
        Assert.Contains(document.TermCounts.Keys, term => term.EndsWith(expectedSuffix, StringComparison.Ordinal));
    }
}
