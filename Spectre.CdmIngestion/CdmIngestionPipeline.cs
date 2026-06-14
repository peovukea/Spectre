namespace Spectre.CdmIngestion;

/// <summary>
/// Describes how an ingestion run ended.
/// </summary>
public enum CdmIngestionOutcome
{
    /// <summary>The validated input set was processed successfully.</summary>
    Completed,

    /// <summary>The run stopped cooperatively because cancellation was requested.</summary>
    Canceled,

    /// <summary>The run stopped because validation, reading, projection, writing, or cleanup failed.</summary>
    Failed
}

/// <summary>
/// Contains cumulative counters and UTC timestamps for one ingestion run.
/// </summary>
public sealed class CdmIngestionMetrics
{
    /// <summary>Gets the number of normalized sourced datums read.</summary>
    public long RecordsRead { get; internal set; }

    /// <summary>Gets the number of facts successfully accepted by the top-level sink.</summary>
    public long FactsWritten { get; internal set; }

    /// <summary>Gets the number of successfully accepted edge facts.</summary>
    public long EdgeFactsWritten { get; internal set; }

    /// <summary>Gets the number of successfully accepted attribute facts.</summary>
    public long AttributeFactsWritten { get; internal set; }

    /// <summary>Gets the number of events skipped for one or more missing required references.</summary>
    public long SkippedEvents { get; internal set; }

    /// <summary>Gets the number of skipped events missing a subject.</summary>
    public long SkippedEventsWithoutSubject { get; internal set; }

    /// <summary>Gets the number of skipped events missing both predicate objects.</summary>
    public long SkippedEventsWithoutObject { get; internal set; }

    /// <summary>Gets the number of valid but unsupported CDM datums skipped.</summary>
    public long SkippedUnknownRecords { get; internal set; }

    /// <summary>Gets the number of supported CDM datums that failed required normalization.</summary>
    public long MalformedRecords { get; internal set; }

    /// <summary>Gets the number of subjects whose subtype required the process fallback.</summary>
    public long UnknownSubjectSubtypeRecords { get; internal set; }

    /// <summary>Gets the number of complete input families processed successfully.</summary>
    public long InputFamiliesProcessed { get; internal set; }

    /// <summary>Gets the number of complete physical segment files processed successfully.</summary>
    public long InputFilesProcessed { get; internal set; }

    /// <summary>Gets the UTC time ingestion started, excluding preflight validation time.</summary>
    public DateTimeOffset? ProcessingStartedAt { get; internal set; }

    /// <summary>Gets the UTC time the completed, canceled, failed, or validation-failed run ended.</summary>
    public DateTimeOffset ProcessingEndedAt { get; internal set; }
}

/// <summary>
/// Contains the outcome, final or partial metrics, and optional failure for one ingestion run.
/// </summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Metrics">Final or partial run metrics.</param>
/// <param name="Exception">Failure exception for failed outcomes; otherwise null.</param>
public sealed record CdmIngestionResult(
    CdmIngestionOutcome Outcome,
    CdmIngestionMetrics Metrics,
    Exception? Exception);

/// <summary>
/// Streams validated CDM families through normalization, projection, and a graph-fact sink.
/// </summary>
/// <param name="reader">Reader that lazily returns normalized sourced datums.</param>
/// <param name="projector">Projector that lazily emits graph facts.</param>
public sealed class CdmFactIngestionPipeline(
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
        IReadOnlyList<CdmInputFamily> families,
        IGraphFactSink sink,
        CdmIngestionMetrics metrics,
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

/// <summary>
/// Coordinates preflight validation, sink lifetime, processing, outcomes, and final metrics.
/// </summary>
/// <param name="pipeline">Pipeline used after successful preflight validation.</param>
public sealed class CdmIngestionRunner(CdmFactIngestionPipeline pipeline)
{
    /// <summary>
    /// Validates inputs and runs ingestion, returning completed, canceled, or failed status.
    /// </summary>
    /// <param name="inputs">Directory or base-family paths to discover and validate globally.</param>
    /// <param name="sinkFactory">
    /// Factory invoked only after all input families validate successfully.
    /// </param>
    /// <param name="cancellationToken">Token used for cooperative cancellation.</param>
    /// <returns>The run outcome, final or partial metrics, and optional failure.</returns>
    public CdmIngestionResult Run(
        IEnumerable<string> inputs,
        Func<IGraphFactSink> sinkFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(sinkFactory);

        var metrics = new CdmIngestionMetrics();
        var outcome = CdmIngestionOutcome.Completed;
        Exception? failure = null;
        IGraphFactSink? sink = null;

        try
        {
            var families = CdmInputFamilyDiscovery.DiscoverAndValidate(inputs);
            cancellationToken.ThrowIfCancellationRequested();

            sink = sinkFactory();
            metrics.ProcessingStartedAt = DateTimeOffset.UtcNow;
            pipeline.Process(families, sink, metrics, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = CdmIngestionOutcome.Canceled;
        }
        catch (Exception exception)
        {
            outcome = CdmIngestionOutcome.Failed;
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
                    outcome = CdmIngestionOutcome.Failed;
                    failure = failure is null
                        ? cleanupException
                        : new AggregateException(failure, cleanupException);
                }
            }

            metrics.ProcessingEndedAt = DateTimeOffset.UtcNow;
        }

        return new CdmIngestionResult(outcome, metrics, failure);
    }
}
