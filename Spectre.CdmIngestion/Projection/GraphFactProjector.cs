using Spectre.CdmIngestion.Pipeline;

namespace Spectre.CdmIngestion.Projection;

/// <summary>
/// Projects normalized sourced CDM datums into typed graph facts.
/// </summary>
public sealed class GraphFactProjector
{
    /// <summary>
    /// Lazily projects a datum while updating projection-related metrics.
    /// </summary>
    /// <param name="datum">Normalized sourced datum to project.</param>
    /// <param name="metrics">Metrics instance updated for skipped, malformed, or diagnostic records.</param>
    /// <returns>Zero or more facts in deterministic emission order.</returns>
    public IEnumerable<GraphFact> Project(SourcedCdmDatum datum, IngestionMetrics metrics)
    {
        switch (datum)
        {
            case SourcedEventDatum eventDatum:
                foreach (var fact in ProjectEvent(eventDatum, metrics))
                {
                    yield return fact;
                }

                yield break;

            case SourcedEntityDatum entityDatum:
                if (entityDatum.UnknownSubjectSubtype)
                {
                    metrics.UnknownSubjectSubtypeRecords++;
                }

                yield return new AttributeFact(
                    entityDatum.EntityId,
                    "HAS_CDM_TYPE",
                    entityDatum.CdmType,
                    TimestampNanos: null,
                    entityDatum.Source);

                yield return new AttributeFact(
                    entityDatum.EntityId,
                    "HAS_NODE_KIND",
                    entityDatum.NodeKind,
                    TimestampNanos: null,
                    entityDatum.Source);

                foreach (var attribute in entityDatum.Attributes.OrderBy(
                             pair => pair.Key,
                             StringComparer.Ordinal))
                {
                    yield return new AttributeFact(
                        entityDatum.EntityId,
                        attribute.Key,
                        attribute.Value,
                        TimestampNanos: null,
                        entityDatum.Source);
                }

                yield break;

            case SourcedUnsupportedDatum:
                metrics.SkippedUnknownRecords++;
                yield break;

            case SourcedMalformedDatum:
                metrics.MalformedRecords++;
                yield break;

            default:
                metrics.SkippedUnknownRecords++;
                yield break;
        }
    }

    private static IEnumerable<GraphFact> ProjectEvent(
        SourcedEventDatum datum,
        IngestionMetrics metrics)
    {
        var missingSubject = datum.SubjectId is null;
        var missingObject = datum.PredicateObjectId is null && datum.PredicateObject2Id is null;

        if (missingSubject || missingObject)
        {
            metrics.SkippedEvents++;

            if (missingSubject)
            {
                metrics.SkippedEventsWithoutSubject++;
            }

            if (missingObject)
            {
                metrics.SkippedEventsWithoutObject++;
            }

            yield break;
        }

        var subjectId = datum.SubjectId.GetValueOrDefault();

        if (datum.PredicateObjectId is { } objectId)
        {
            yield return new EdgeFact(
                subjectId,
                datum.EventType,
                objectId,
                datum.TimestampNanos,
                datum.Source,
                datum.EventId);
        }

        if (datum.PredicateObject2Id is { } object2Id)
        {
            yield return new EdgeFact(
                subjectId,
                datum.EventType,
                object2Id,
                datum.TimestampNanos,
                datum.Source,
                datum.EventId);
        }
    }
}
