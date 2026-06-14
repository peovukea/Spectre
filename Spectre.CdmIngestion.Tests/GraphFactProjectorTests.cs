using Spectre.CdmIngestion;

namespace Spectre.CdmIngestion.Tests;

public sealed class GraphFactProjectorTests
{
    private static readonly SourceLocation Source = new("segment.bin", 12);

    [Fact]
    public void Entity_EmitsFixedFactsBeforeOrdinalAttributes()
    {
        var datum = new SourcedEntityDatum(
            Guid.NewGuid(),
            "Subject",
            "SUBJECT_THREAD",
            new Dictionary<string, string>
            {
                ["HAS_ZETA"] = "z",
                ["HAS_ALPHA"] = "a",
                ["HAS_SUBJECT_TYPE"] = "SUBJECT_THREAD"
            },
            UnknownSubjectSubtype: false,
            Source);
        var metrics = new CdmIngestionMetrics();

        var predicates = new GraphFactProjector()
            .Project(datum, metrics)
            .Select(fact => fact.Predicate)
            .ToArray();

        Assert.Equal(
            ["HAS_CDM_TYPE", "HAS_NODE_KIND", "HAS_ALPHA", "HAS_SUBJECT_TYPE", "HAS_ZETA"],
            predicates);
    }

    [Fact]
    public void Event_MissingSubjectAndObjects_UpdatesEveryApplicableCounter()
    {
        var datum = new SourcedEventDatum(
            EventId: null,
            SubjectId: null,
            PredicateObjectId: null,
            PredicateObject2Id: null,
            "EVENT_READ",
            123,
            Source);
        var metrics = new CdmIngestionMetrics();

        var facts = new GraphFactProjector().Project(datum, metrics).ToArray();

        Assert.Empty(facts);
        Assert.Equal(1, metrics.SkippedEvents);
        Assert.Equal(1, metrics.SkippedEventsWithoutSubject);
        Assert.Equal(1, metrics.SkippedEventsWithoutObject);
    }

    [Fact]
    public void Event_EmitsPredicateObjectBeforePredicateObject2()
    {
        var firstObject = Guid.NewGuid();
        var secondObject = Guid.NewGuid();
        var datum = new SourcedEventDatum(
            Guid.NewGuid(),
            Guid.NewGuid(),
            firstObject,
            secondObject,
            "EVENT_RENAME",
            123,
            Source);

        var facts = new GraphFactProjector()
            .Project(datum, new CdmIngestionMetrics())
            .Cast<EdgeFact>()
            .ToArray();

        Assert.Equal([firstObject, secondObject], facts.Select(fact => fact.ObjectId));
    }
}
