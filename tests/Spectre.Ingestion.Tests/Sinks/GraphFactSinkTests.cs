using Spectre.Ingestion.Sinks;

namespace Spectre.Ingestion.Tests.Sinks;

public sealed class GraphFactSinkTests
{
    [Fact]
    public void SampleCap_IsSuccessfulNoOpAndCompositeContinuesForwarding()
    {
        using var temp = new TempDirectory();
        var samplePath = temp.File("sample.jsonl");
        var collecting = new CollectingSink();
        var source = new SourceLocation("segment.bin", 1);
        var fact = new AttributeFact(Guid.NewGuid(), "HAS_TYPE", "x", null, source);

        using (var sink = new CompositeGraphFactSink(
               [
                   new SampleJsonlGraphFactSink(samplePath, sampleLimit: 1),
                   collecting
               ]))
        {
            sink.Write(fact);
            sink.Write(fact);
        }

        Assert.Equal(2, collecting.Facts.Count);
        Assert.Single(System.IO.File.ReadAllLines(samplePath));
    }

    [Fact]
    public void Composite_StopsAfterFirstThrowingChild()
    {
        var first = new CollectingSink();
        var last = new CollectingSink();
        var sink = new CompositeGraphFactSink([first, new ThrowingSink(), last]);
        var fact = new AttributeFact(
            Guid.NewGuid(),
            "HAS_TYPE",
            "x",
            null,
            new SourceLocation("segment.bin", 1));

        Assert.Throws<InvalidOperationException>(() => sink.Write(fact));
        Assert.Single(first.Facts);
        Assert.Empty(last.Facts);
    }

    [Fact]
    public void Composite_ForwardsFamilyLifecycleOnlyToFamilyAwareChildren()
    {
        var familyAware = new FamilyTrackingSink();
        using var sink = new CompositeGraphFactSink([new CollectingSink(), familyAware]);

        sink.BeginFamily("family.bin");
        sink.EndFamily("family.bin");

        Assert.Equal(["BEGIN:family.bin", "END:family.bin"], familyAware.Events);
    }

    private sealed class ThrowingSink : IGraphFactSink
    {
        public void Write(GraphFact fact) => throw new InvalidOperationException("sink failed");

        public void Dispose()
        {
        }
    }

    private sealed class FamilyTrackingSink : IGraphFactFamilySink
    {
        public List<string> Events { get; } = [];

        public void BeginFamily(string familyBasePath) => Events.Add($"BEGIN:{familyBasePath}");

        public void EndFamily(string familyBasePath) => Events.Add($"END:{familyBasePath}");

        public void Write(GraphFact fact)
        {
        }

        public void Dispose()
        {
        }
    }
}
