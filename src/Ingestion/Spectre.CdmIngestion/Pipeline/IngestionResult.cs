namespace Spectre.CdmIngestion.Pipeline;

/// <summary>
/// The outcome, final or partial metrics, and optional failure for one ingestion run.
/// </summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Metrics">Final or partial run metrics.</param>
/// <param name="Exception">Failure exception for failed outcomes; otherwise null.</param>
public sealed record IngestionResult(
    IngestionOutcome Outcome,
    IngestionMetrics Metrics,
    Exception? Exception);
