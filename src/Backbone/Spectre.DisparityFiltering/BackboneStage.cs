using Spectre.Backbone.Api.Sinks;
using Spectre.DisparityFiltering.Sinks;
using Spectre.Indexing.Api.Sinks;

namespace Spectre.DisparityFiltering;

public sealed class BackboneStage : IBackboneStage
{
    public ISemanticGraphSliceSink CreateSink(IDisparityGraphSliceSink output, DisparityFilterOptions? options = null) =>
        new DisparityFilteringSemanticGraphSliceSink(output, options);
}
