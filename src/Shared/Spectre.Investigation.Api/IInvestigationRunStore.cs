using Spectre.Backbone.Api;
using Spectre.Indexing.Api;

namespace Spectre.Investigation.Api;

/// <summary>
/// Write-side stage API for recording run lifecycle and persisted backbone slices.
/// </summary>
public interface IInvestigationRunStore
{
    void MarkRunState(
        RunState state,
        bool isPartial = false,
        SemanticIndexingMetrics? indexingMetrics = null,
        DisparityFilteringMetrics? filteringMetrics = null);

    void MarkWritesClosed();
    void RecoverInterruptedRuns();
    void AcceptSlice(DisparityGraphSlice slice);
}
