using Spectre.CdmIngestion;
using Spectre.SemanticIndexing.Sinks;

namespace Spectre.SemanticIndexing.Tests;

public class InMemorySemanticGraphSliceSink : ISemanticGraphSliceSink
{
    public List<SemanticGraphSlice> Slices { get; } = [];
    public bool Disposed { get; private set; }

    public void Write(SemanticGraphSlice slice) => Slices.Add(slice);

    public void Dispose() => Disposed = true;
}

internal sealed class CollectingSliceSink : InMemorySemanticGraphSliceSink
{
}

internal static class Fact
{
    public static readonly SourceLocation Source = new("segment.bin", 12);

    public static AttributeFact Attribute(Guid id, string predicate, string value) =>
        new(id, predicate, value, null, Source);

    public static EdgeFact Edge(
        Guid source,
        Guid target,
        long? timestamp,
        string predicate = "EVENT_READ",
        Guid? eventId = null) =>
        new(source, predicate, target, timestamp, Source, eventId);

    public static SemanticIndexingGraphFactSink Indexer(
        CollectingSliceSink output,
        long windowNanos = 1_000,
        long latenessNanos = 0,
        int evidenceCap = 3) =>
        new(output, new SemanticIndexingOptions
        {
            WindowSize = TimeSpan.FromTicks(windowNanos / 100),
            AllowedLateness = TimeSpan.FromTicks(latenessNanos / 100),
            MaxEvidencePointersPerInteraction = evidenceCap
        });
}
