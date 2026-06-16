using Spectre.DisparityFiltering;
using Spectre.SemanticIndexing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spectre.InvestigationHost.Store;

public enum RunState { NotStarted, Running, Completed, Failed, Canceled }

public sealed record RunStatusDto(
    RunState State,
    long ElapsedSeconds,
    bool IsPartial,
    SemanticIndexingMetrics? IndexingMetrics,
    DisparityFilteringMetrics? FilteringMetrics);

public sealed record RunInfoDto(
    long Id,
    string StartedAtUtc,
    string? CompletedAtUtc,
    RunState State,
    long ElapsedSeconds,
    bool IsPartial,
    int FamilyCount,
    int WindowCount);

public sealed record FamilyInfoDto(
    int Id,
    string Key,
    string Name,
    long FirstWindowStartNanos,
    long LastWindowStartNanos);

public sealed record StoreMemoryPressureDto(
    int RetainedDetailedSlices,
    int RetainedProjections,
    int TotalSummaries,
    long EstimatedDetailedBytes,
    long EstimatedProjectionBytes,
    long DetailedBytesLimit,
    long ProjectionBytesLimit,
    int EvictedDetailedSlices,
    int EvictedProjections,
    long GcTotalMemoryBytes,
    long WorkingSetBytes,
    long GcHeapSizeBytes);

public sealed record GraphQueryParameters(
    double MinWeight = 0.0,
    int MaxNodes = 250,
    int MaxEdges = 200,
    string? Predicate = null,
    string? NodeKind = null);

public enum StoreQueryStatus { Found, NotFound, Gone }

public sealed record StoreQueryResult<T>(StoreQueryStatus Status, T? Value)
{
    public static StoreQueryResult<T> Found(T value) => new(StoreQueryStatus.Found, value);
    public static StoreQueryResult<T> NotFound() => new(StoreQueryStatus.NotFound, default);
    public static StoreQueryResult<T> Gone() => new(StoreQueryStatus.Gone, default);
}

public enum SliceRetentionLevel { Summary, Projection, Detailed }

public sealed record JaccardDistributionDto(
    int Count, int NullCount,
    double Min, double Max, double Mean,
    double P25, double P50, double P75);

public sealed record SliceSummaryDto(
    int FamilyId,
    string FamilyKey,
    string FamilyName,
    long WindowStartNanos,
    long WindowEndNanos,
    string WindowStartIso,
    int DocumentCount,
    int InteractionCount,
    double MaxSemanticWeight,
    double TotalSemanticWeight,
    IReadOnlyDictionary<string, int> PredicateCounts,
    IReadOnlyDictionary<string, int> NodeKindCounts,
    JaccardDistributionDto JaccardNodeKind,
    JaccardDistributionDto JaccardPreviousSelf,
    DisparitySliceReduction Reduction,
    SliceRetentionLevel RetentionLevel);

public sealed record ProjectedNodeDto(
    string Id, string Kind, string Label,
    double? JaccardNodeKind, double? JaccardPreviousSelf);

public sealed record ProjectedEdgeDto(
    string Source, string Target,
    int Count, double SemanticWeight,
    IReadOnlyDictionary<string, int> PredicateCounts,
    DirectionalDisparityScore SourceOutgoing,
    DirectionalDisparityScore TargetIncoming);

public sealed record GraphProjectionDto(
    IReadOnlyList<ProjectedNodeDto> Nodes,
    IReadOnlyList<ProjectedEdgeDto> Edges,
    bool Truncated,
    int TotalMatchingEdges,
    int AppliedMaxNodes,
    int AppliedMaxEdges,
    SliceRetentionLevel RetentionLevel);

public sealed record NodeDetailDto(
    string Id, string Kind, string Label,
    double? JaccardNodeKind, double? JaccardPreviousSelf,
    IReadOnlyDictionary<string, int> TermCounts,
    IReadOnlyDictionary<string, double> TfidfWeights);

public sealed record EvidencePointerDto(
    string SegmentPath, long SyncBlockOffset,
    long? TimestampNanos, string? EventId);

public sealed record InteractionDetailDto(
    string SourceId, string TargetId,
    int Count, double SemanticWeight,
    IReadOnlyDictionary<string, int> PredicateCounts,
    IReadOnlyDictionary<string, double> PredicateSemanticWeights,
    IReadOnlyDictionary<string, int> TermCounts,
    IReadOnlyList<EvidencePointerDto> Evidence,
    DirectionalDisparityScore SourceOutgoing,
    DirectionalDisparityScore TargetIncoming);

public sealed class LongAsStringJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? long.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : reader.GetInt64();

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
