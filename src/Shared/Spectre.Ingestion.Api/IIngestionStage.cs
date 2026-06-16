using Spectre.Ingestion.Api.Pipeline;
using Spectre.Ingestion.Api.Sinks;

namespace Spectre.Ingestion.Api;

/// <summary>
/// Stage API for validating inputs and emitting graph facts into a supplied sink.
/// </summary>
public interface IIngestionStage
{
    /// <summary>
    /// Runs ingestion for one or more inputs.
    /// </summary>
    IngestionResult Run(
        IEnumerable<string> inputs,
        Func<IGraphFactSink> sinkFactory,
        CancellationToken cancellationToken = default);
}
