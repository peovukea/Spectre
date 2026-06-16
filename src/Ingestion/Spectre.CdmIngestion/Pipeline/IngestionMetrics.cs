namespace Spectre.CdmIngestion.Pipeline;

/// <summary>
/// Cumulative counters and UTC timestamps for one ingestion run.
/// </summary>
public sealed class IngestionMetrics
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
