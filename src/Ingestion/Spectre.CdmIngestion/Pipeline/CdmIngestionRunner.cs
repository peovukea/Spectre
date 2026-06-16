namespace Spectre.CdmIngestion.Pipeline;

/// <summary>
/// Coordinates preflight validation, sink lifetime, processing, outcomes, and final metrics.
/// </summary>
/// <param name="pipeline">Pipeline used after successful preflight validation.</param>
public sealed class CdmIngestionRunner(IngestionPipeline pipeline) : IIngestionStage
{
    /// <summary>
    /// Validates inputs and runs ingestion, returning completed, cancelled, or failed status.
    /// </summary>
    /// <param name="inputs">Directory or base-family paths to discover and validate globally.</param>
    /// <param name="sinkFactory">
    /// Factory invoked only after all input families validate successfully.
    /// </param>
    /// <param name="cancellationToken">Token used for cooperative cancellation.</param>
    /// <returns>The run outcome, final or partial metrics, and optional failure.</returns>
    public IngestionResult Run(
        IEnumerable<string> inputs,
        Func<IGraphFactSink> sinkFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(sinkFactory);

        var metrics = new IngestionMetrics();
        var outcome = IngestionOutcome.Completed;
        Exception? failure = null;
        IGraphFactSink? sink = null;

        try
        {
            var families = CdmInputFamilyDiscovery.Resolve(inputs);
            cancellationToken.ThrowIfCancellationRequested();

            sink = sinkFactory();
            metrics.ProcessingStartedAt = DateTimeOffset.UtcNow;
            pipeline.Process(families, sink, metrics, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = IngestionOutcome.Canceled;
        }
        catch (Exception exception)
        {
            outcome = IngestionOutcome.Failed;
            failure = exception;
        }
        finally
        {
            if (sink is not null)
            {
                try
                {
                    sink.Dispose();
                }
                catch (Exception cleanupException)
                {
                    outcome = IngestionOutcome.Failed;
                    failure = failure is null
                        ? cleanupException
                        : new AggregateException(failure, cleanupException);
                }
            }

            metrics.ProcessingEndedAt = DateTimeOffset.UtcNow;
        }

        return new IngestionResult(outcome, metrics, failure);
    }
}
