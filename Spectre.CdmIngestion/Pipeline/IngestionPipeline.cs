using Spectre.CdmIngestion.Projection;
using Spectre.CdmIngestion.Sinks;

namespace Spectre.CdmIngestion.Pipeline;

/// <summary>
/// Streams validated CDM families through normalization, projection, and a graph-fact sink.
/// </summary>
/// <param name="reader">Reader that lazily returns normalized sourced datums.</param>
/// <param name="projector">Projector that lazily emits graph facts.</param>
public sealed class IngestionPipeline(
    ICdmRecordReader reader,
    GraphFactProjector projector)
{
    /// <summary>
    /// Processes validated families synchronously and lazily.
    /// </summary>
    /// <param name="families">Prevalidated families in processing order.</param>
    /// <param name="sink">Top-level sink that accepts emitted facts.</param>
    /// <param name="metrics">Metrics instance updated during processing.</param>
    /// <param name="cancellationToken">Token checked between datums and before sink writes.</param>
    public void Process(
        IReadOnlyList<InputFamily> families,
        IGraphFactSink sink,
        IngestionMetrics metrics,
        CancellationToken cancellationToken)
    {
        foreach (var family in families)
        {
            foreach (var segmentPath in family.SegmentPaths)
            {
                foreach (var datum in reader.ReadFile(segmentPath, cancellationToken))
                {
                    metrics.RecordsRead++;
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var fact in projector.Project(datum, metrics))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sink.Write(fact);

                        metrics.FactsWritten++;
                        if (fact is EdgeFact)
                        {
                            metrics.EdgeFactsWritten++;
                        }
                        else if (fact is AttributeFact)
                        {
                            metrics.AttributeFactsWritten++;
                        }
                    }
                }

                metrics.InputFilesProcessed++;
            }

            metrics.InputFamiliesProcessed++;
        }
    }
}
