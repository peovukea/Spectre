using Spectre.DisparityFiltering;
using Spectre.SemanticIndexing;

namespace Spectre.InvestigationHost.Store;

public interface IInvestigationStore
{
    int DefaultMaxNodes { get; }
    int DefaultMaxEdges { get; }

    void MarkRunState(
        RunState state,
        bool isPartial = false,
        SemanticIndexingMetrics? indexingMetrics = null,
        DisparityFilteringMetrics? filteringMetrics = null);

    void MarkWritesClosed();
    void RecoverInterruptedRuns();
    void AcceptSlice(DisparityGraphSlice slice);

    IReadOnlyList<RunInfoDto> GetRuns();
    RunStatusDto GetRunStatus(long? runId = null);
    StoreMemoryPressureDto GetMemoryPressure();
    IReadOnlyList<FamilyInfoDto> GetFamilies(long? runId = null);
    IReadOnlyList<SliceSummaryDto> GetWindows(int familyId, long? runId = null);
    IReadOnlySet<string> GetPredicates(long? runId = null);
    IReadOnlySet<string> GetNodeKinds(long? runId = null);
    StoreQueryResult<GraphProjectionDto> GetProjection(int familyId, long windowStart, GraphQueryParameters parameters, long? runId = null);
    StoreQueryResult<NodeDetailDto> GetNodeDetail(int familyId, long windowStart, Guid nodeId, long? runId = null);
    StoreQueryResult<InteractionDetailDto> GetInteractionDetail(int familyId, long windowStart, Guid source, Guid target, long? runId = null);
}
