namespace Spectre.Investigation.Api;

/// <summary>
/// Read-side stage API for investigation runs and graph exploration.
/// </summary>
public interface IInvestigationQueryService
{
    int DefaultMaxNodes { get; }
    int DefaultMaxEdges { get; }

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
