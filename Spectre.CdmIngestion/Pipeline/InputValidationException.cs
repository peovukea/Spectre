namespace Spectre.CdmIngestion.Pipeline;

/// <summary>
/// Reports a pre-ingestion CDM input-family layout validation failure.
/// </summary>
public sealed class InputValidationException(string message) : Exception(message);
