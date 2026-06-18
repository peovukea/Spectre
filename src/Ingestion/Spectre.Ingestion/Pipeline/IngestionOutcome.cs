namespace Spectre.Ingestion.Pipeline;

/// <summary>
/// Describes how an ingestion run ended.
/// </summary>
public enum IngestionOutcome
{
    /// <summary>The validated input set was processed successfully.</summary>
    Completed,

    /// <summary>The run stopped cooperatively because cancellation was requested.</summary>
    Canceled,

    /// <summary>The run stopped because validation, reading, projection, writing, or cleanup failed.</summary>
    Failed
}
