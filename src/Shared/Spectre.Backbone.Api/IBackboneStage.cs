using Spectre.Backbone.Api.Sinks;
using Spectre.Indexing.Api.Sinks;

namespace Spectre.Backbone.Api;

/// <summary>
/// Stage API for reducing semantic graph slices into disparity-filtered backbone slices.
/// </summary>
public interface IBackboneStage
{
    /// <summary>
    /// Creates a semantic slice sink that owns the supplied downstream backbone sink.
    /// </summary>
    ISemanticGraphSliceSink CreateSink(IDisparityGraphSliceSink output, DisparityFilterOptions? options = null);
}
