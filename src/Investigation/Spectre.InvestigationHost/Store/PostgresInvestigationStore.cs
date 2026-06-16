using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Spectre.InvestigationHost.Store;

public sealed class PostgresInvestigationStore : IInvestigationRunStore, IInvestigationQueryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new LongAsStringJsonConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly EventHub _eventHub;
    private readonly object _runLock = new();
    private readonly Stopwatch _runTimer = new();

    private long? _runId;
    private RunState _runState = RunState.NotStarted;
    private bool _isPartial;
    private SemanticIndexingMetrics? _metricsSnapshot;
    private DisparityFilteringMetrics? _filteringMetricsSnapshot;

    public int DefaultMaxNodes { get; init; } = 250;
    public int DefaultMaxEdges { get; init; } = 200;

    public PostgresInvestigationStore(NpgsqlDataSource dataSource, EventHub eventHub)
    {
        _dataSource = dataSource;
        _eventHub = eventHub;
    }

    public void MarkRunState(
        RunState state,
        bool isPartial = false,
        SemanticIndexingMetrics? indexingMetrics = null,
        DisparityFilteringMetrics? filteringMetrics = null)
    {
        lock (_runLock)
        {
            if (state == RunState.Running && (_runId is null || _runState != RunState.Running))
            {
                _runId = CreateRun();
                _runTimer.Restart();
                _isPartial = false;
                _metricsSnapshot = null;
                _filteringMetricsSnapshot = null;
            }
            else if (state is RunState.Completed or RunState.Failed or RunState.Canceled)
            {
                _runTimer.Stop();
            }

            _runState = state;
            _isPartial |= isPartial;
            if (indexingMetrics is not null) _metricsSnapshot = indexingMetrics;
            if (filteringMetrics is not null) _filteringMetricsSnapshot = filteringMetrics;

            if (_runId is not null)
            {
                UpdateRun(_runId.Value, state, _isPartial, _metricsSnapshot, _filteringMetricsSnapshot);
            }
        }

        PublishRunStatus();
    }

    public void MarkWritesClosed()
    {
    }

    public void RecoverInterruptedRuns()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
UPDATE investigation_runs
SET state = @state,
    completed_at_utc = COALESCE(completed_at_utc, now()),
    is_partial = true
WHERE state = @running_state
""", connection);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, RunState.Failed.ToString());
        command.Parameters.AddWithValue("running_state", NpgsqlDbType.Text, RunState.Running.ToString());
        command.ExecuteNonQuery();
    }

    public void AcceptSlice(DisparityGraphSlice slice)
    {
        long runId = EnsureActiveRun();
        SliceSummaryDto summary;

        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            var basePath = slice.InputFamilyBasePath ?? "unknown";
            var (familyKey, familyName) = FamilyKeyResolver.Resolve(basePath);
            var family = UpsertFamily(connection, transaction, runId, familyKey, familyName, slice.WindowStartNanos);

            summary = BuildSummary(family.Id, family.Key, family.Name, slice, SliceRetentionLevel.Detailed);
            UpsertSummary(connection, transaction, runId, summary);
            ReplaceSliceRows(connection, transaction, runId, family.Id, slice);

            transaction.Commit();

            lock (_runLock)
            {
                _metricsSnapshot = slice.IndexingMetrics;
                _filteringMetricsSnapshot = slice.Metrics;
                if (_runId is not null)
                {
                    UpdateRun(_runId.Value, _runState, _isPartial, _metricsSnapshot, _filteringMetricsSnapshot);
                }
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        _eventHub.Publish(new ServerSentEvent("slice-closed", JsonSerializer.Serialize(summary, JsonOptions)));
        _eventHub.Publish(new ServerSentEvent("memory-pressure", JsonSerializer.Serialize(GetMemoryPressure(), JsonOptions)));
    }

    public IReadOnlyList<RunInfoDto> GetRuns()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT r.id, r.started_at_utc, r.completed_at_utc, r.state, r.elapsed_seconds, r.is_partial,
       COALESCE(f.family_count, 0), COALESCE(s.window_count, 0)
FROM investigation_runs r
LEFT JOIN (
    SELECT run_id, count(*)::int AS family_count
    FROM families
    GROUP BY run_id
) f ON f.run_id = r.id
LEFT JOIN (
    SELECT run_id, count(*)::int AS window_count
    FROM slice_summaries
    GROUP BY run_id
) s ON s.run_id = r.id
ORDER BY r.started_at_utc DESC, r.id DESC
LIMIT 100
""", connection);

        var runs = new List<RunInfoDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(new RunInfoDto(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime().ToString("o"),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime().ToString("o"),
                Enum.Parse<RunState>(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetBoolean(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }

        return runs;
    }

    public RunStatusDto GetRunStatus(long? runId = null)
    {
        lock (_runLock)
        {
            if (_runId is not null && (runId is null || runId == _runId))
            {
                return new RunStatusDto(
                    _runState,
                    (long)_runTimer.Elapsed.TotalSeconds,
                    _isPartial,
                    _metricsSnapshot?.Snapshot(),
                    _filteringMetricsSnapshot?.Snapshot());
            }
        }

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT state, elapsed_seconds, is_partial, indexing_metrics::text, filtering_metrics::text
FROM investigation_runs
WHERE (@run_id IS NULL OR id = @run_id)
ORDER BY started_at_utc DESC, id DESC
LIMIT 1
""", connection);
        command.Parameters.Add("run_id", NpgsqlDbType.Bigint).Value = (object?)runId ?? DBNull.Value;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new RunStatusDto(RunState.NotStarted, 0, false, null, null);
        }

        return new RunStatusDto(
            Enum.Parse<RunState>(reader.GetString(0)),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : Deserialize<SemanticIndexingMetrics>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Deserialize<DisparityFilteringMetrics>(reader.GetString(4)));
    }

    public StoreMemoryPressureDto GetMemoryPressure()
    {
        var runId = GetLatestRunId();
        if (runId is null)
        {
            return BuildMemoryPressure(0);
        }

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT count(*) FROM slice_summaries WHERE run_id = @run_id", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        var summaries = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return BuildMemoryPressure(summaries);
    }

    public IReadOnlyList<FamilyInfoDto> GetFamilies(long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return [];

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT family_id, key, name, first_window_start_nanos, last_window_start_nanos
FROM families
WHERE run_id = @run_id
ORDER BY family_id
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);

        var families = new List<FamilyInfoDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            families.Add(new FamilyInfoDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        }

        return families;
    }

    public IReadOnlyList<SliceSummaryDto> GetWindows(int familyId, long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return [];

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT family_id, family_key, family_name, window_start_nanos, window_end_nanos, window_start_iso,
       document_count, interaction_count, max_semantic_weight, total_semantic_weight,
       predicate_counts::text, node_kind_counts::text, jaccard_node_kind::text, jaccard_previous_self::text,
       reduction::text, retention_level
FROM slice_summaries
WHERE run_id = @run_id AND family_id = @family_id
ORDER BY window_start_nanos
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);

        var windows = new List<SliceSummaryDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            windows.Add(ReadSummary(reader));
        }

        return windows;
    }

    public IReadOnlySet<string> GetPredicates(long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return new HashSet<string>();

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT DISTINCT jsonb_object_keys(predicate_counts)
FROM slice_summaries
WHERE run_id = @run_id
ORDER BY 1
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        return ReadStringSet(command);
    }

    public IReadOnlySet<string> GetNodeKinds(long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return new HashSet<string>();

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT DISTINCT jsonb_object_keys(node_kind_counts)
FROM slice_summaries
WHERE run_id = @run_id
ORDER BY 1
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        return ReadStringSet(command);
    }

    public StoreQueryResult<GraphProjectionDto> GetProjection(int familyId, long windowStart, GraphQueryParameters parameters, long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return StoreQueryResult<GraphProjectionDto>.NotFound();

        var summaryStatus = GetSummaryRetention(runId.Value, familyId, windowStart);
        if (summaryStatus is null) return StoreQueryResult<GraphProjectionDto>.NotFound();
        if (summaryStatus != SliceRetentionLevel.Detailed) return StoreQueryResult<GraphProjectionDto>.Gone();

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT source_node_id, target_node_id, count, semantic_weight, predicate_counts::text,
       source_degree, source_strength, source_normalized_weight, source_significance, source_is_significant,
       target_degree, target_strength, target_normalized_weight, target_significance, target_is_significant
FROM backbone_interactions i
WHERE i.run_id = @run_id
  AND i.family_id = @family_id
  AND i.window_start_nanos = @window_start_nanos
  AND i.semantic_weight >= @min_weight
  AND (@predicate IS NULL OR i.predicate_counts ? @predicate)
  AND (@node_kind IS NULL OR EXISTS (
      SELECT 1
      FROM node_documents d
      WHERE d.run_id = i.run_id
        AND d.family_id = i.family_id
        AND d.window_start_nanos = i.window_start_nanos
        AND d.node_id IN (i.source_node_id, i.target_node_id)
        AND d.node_kind = @node_kind))
ORDER BY i.semantic_weight DESC, i.source_node_id, i.target_node_id
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
        command.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, windowStart);
        command.Parameters.AddWithValue("min_weight", NpgsqlDbType.Double, parameters.MinWeight);
        command.Parameters.Add("predicate", NpgsqlDbType.Text).Value = (object?)parameters.Predicate ?? DBNull.Value;
        command.Parameters.Add("node_kind", NpgsqlDbType.Text).Value = (object?)parameters.NodeKind ?? DBNull.Value;

        var acceptedEdges = new List<ProjectedEdgeDto>();
        var acceptedNodeIds = new HashSet<Guid>();
        var totalMatchingEdges = 0;
        var truncated = false;

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                totalMatchingEdges++;
                var source = reader.GetGuid(0);
                var target = reader.GetGuid(1);
                var extraNodes = (acceptedNodeIds.Contains(source) ? 0 : 1) + (acceptedNodeIds.Contains(target) ? 0 : 1);

                if (acceptedNodeIds.Count + extraNodes <= parameters.MaxNodes && acceptedEdges.Count < parameters.MaxEdges)
                {
                    acceptedNodeIds.Add(source);
                    acceptedNodeIds.Add(target);
                    acceptedEdges.Add(new ProjectedEdgeDto(
                        source.ToString("D"),
                        target.ToString("D"),
                        reader.GetInt32(2),
                        reader.GetDouble(3),
                        Deserialize<Dictionary<string, int>>(reader.GetString(4)),
                        ReadScore(reader, 5),
                        ReadScore(reader, 10)));
                }
                else
                {
                    truncated = true;
                }
            }
        }

        var nodes = GetProjectedNodes(connection, runId.Value, familyId, windowStart, acceptedNodeIds);
        return StoreQueryResult<GraphProjectionDto>.Found(new GraphProjectionDto(nodes, acceptedEdges, truncated, totalMatchingEdges, parameters.MaxNodes, parameters.MaxEdges, SliceRetentionLevel.Detailed));
    }

    public StoreQueryResult<NodeDetailDto> GetNodeDetail(int familyId, long windowStart, Guid nodeId, long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return StoreQueryResult<NodeDetailDto>.NotFound();

        var summaryStatus = GetSummaryRetention(runId.Value, familyId, windowStart);
        if (summaryStatus is null) return StoreQueryResult<NodeDetailDto>.NotFound();
        if (summaryStatus != SliceRetentionLevel.Detailed) return StoreQueryResult<NodeDetailDto>.Gone();

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT node_kind, jaccard_node_kind, jaccard_previous_self, term_counts::text, tfidf_weights::text
FROM node_documents
WHERE run_id = @run_id AND family_id = @family_id AND window_start_nanos = @window_start_nanos AND node_id = @node_id
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
        command.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, windowStart);
        command.Parameters.AddWithValue("node_id", NpgsqlDbType.Uuid, nodeId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return StoreQueryResult<NodeDetailDto>.NotFound();

        return StoreQueryResult<NodeDetailDto>.Found(new NodeDetailDto(
            nodeId.ToString("D"),
            reader.GetString(0),
            nodeId.ToString("D")[..8],
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            Deserialize<Dictionary<string, int>>(reader.GetString(3)),
            Deserialize<Dictionary<string, double>>(reader.GetString(4))));
    }

    public StoreQueryResult<InteractionDetailDto> GetInteractionDetail(int familyId, long windowStart, Guid source, Guid target, long? requestedRunId = null)
    {
        var runId = ResolveRunId(requestedRunId);
        if (runId is null) return StoreQueryResult<InteractionDetailDto>.NotFound();

        var summaryStatus = GetSummaryRetention(runId.Value, familyId, windowStart);
        if (summaryStatus is null) return StoreQueryResult<InteractionDetailDto>.NotFound();
        if (summaryStatus != SliceRetentionLevel.Detailed) return StoreQueryResult<InteractionDetailDto>.Gone();

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT count, semantic_weight, predicate_counts::text, predicate_semantic_weights::text, term_counts::text, evidence::text,
       source_degree, source_strength, source_normalized_weight, source_significance, source_is_significant,
       target_degree, target_strength, target_normalized_weight, target_significance, target_is_significant
FROM backbone_interactions
WHERE run_id = @run_id AND family_id = @family_id AND window_start_nanos = @window_start_nanos
  AND source_node_id = @source_node_id AND target_node_id = @target_node_id
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId.Value);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
        command.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, windowStart);
        command.Parameters.AddWithValue("source_node_id", NpgsqlDbType.Uuid, source);
        command.Parameters.AddWithValue("target_node_id", NpgsqlDbType.Uuid, target);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return StoreQueryResult<InteractionDetailDto>.NotFound();

        return StoreQueryResult<InteractionDetailDto>.Found(new InteractionDetailDto(
            source.ToString("D"),
            target.ToString("D"),
            reader.GetInt32(0),
            reader.GetDouble(1),
            Deserialize<Dictionary<string, int>>(reader.GetString(2)),
            Deserialize<Dictionary<string, double>>(reader.GetString(3)),
            Deserialize<Dictionary<string, int>>(reader.GetString(4)),
            Deserialize<List<EvidencePointerDto>>(reader.GetString(5)),
            ReadScore(reader, 6),
            ReadScore(reader, 11)));
    }

    private long EnsureActiveRun()
    {
        lock (_runLock)
        {
            if (_runId is null)
            {
                _runId = CreateRun();
                _runState = RunState.Running;
                _runTimer.Restart();
                UpdateRun(_runId.Value, _runState, _isPartial, _metricsSnapshot, _filteringMetricsSnapshot);
            }

            return _runId.Value;
        }
    }

    private long CreateRun()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("INSERT INTO investigation_runs (state) VALUES (@state) RETURNING id", connection);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, RunState.Running.ToString());
        return (long)command.ExecuteScalar()!;
    }

    private void UpdateRun(long runId, RunState state, bool isPartial, SemanticIndexingMetrics? indexingMetrics, DisparityFilteringMetrics? filteringMetrics)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
UPDATE investigation_runs
SET state = @state,
    completed_at_utc = CASE WHEN @is_terminal THEN now() ELSE completed_at_utc END,
    elapsed_seconds = @elapsed_seconds,
    is_partial = @is_partial,
    indexing_metrics = @indexing_metrics,
    filtering_metrics = @filtering_metrics
WHERE id = @id
""", connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, runId);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, state.ToString());
        command.Parameters.AddWithValue("is_terminal", NpgsqlDbType.Boolean, state is RunState.Completed or RunState.Failed or RunState.Canceled);
        command.Parameters.AddWithValue("elapsed_seconds", NpgsqlDbType.Bigint, (long)_runTimer.Elapsed.TotalSeconds);
        command.Parameters.AddWithValue("is_partial", NpgsqlDbType.Boolean, isPartial);
        command.Parameters.Add("indexing_metrics", NpgsqlDbType.Jsonb).Value = indexingMetrics is null ? DBNull.Value : JsonSerializer.Serialize(indexingMetrics.Snapshot(), JsonOptions);
        command.Parameters.Add("filtering_metrics", NpgsqlDbType.Jsonb).Value = filteringMetrics is null ? DBNull.Value : JsonSerializer.Serialize(filteringMetrics.Snapshot(), JsonOptions);
        command.ExecuteNonQuery();
    }

    private long? GetLatestRunId()
    {
        lock (_runLock)
        {
            if (_runId is not null) return _runId.Value;
        }

        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT id FROM investigation_runs ORDER BY started_at_utc DESC, id DESC LIMIT 1", connection);
        var result = command.ExecuteScalar();
        return result is null ? null : (long)result;
    }

    private long? ResolveRunId(long? requestedRunId) => requestedRunId ?? GetLatestRunId();

    private FamilyInfoDto UpsertFamily(NpgsqlConnection connection, NpgsqlTransaction transaction, long runId, string familyKey, string familyName, long windowStart)
    {
        using (var select = new NpgsqlCommand("""
SELECT family_id, first_window_start_nanos, last_window_start_nanos
FROM families
WHERE run_id = @run_id AND key = @key
""", connection, transaction))
        {
            select.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
            select.Parameters.AddWithValue("key", NpgsqlDbType.Text, familyKey);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                var familyId = reader.GetInt32(0);
                var first = reader.GetInt64(1);
                var last = Math.Max(reader.GetInt64(2), windowStart);
                reader.Close();

                using var update = new NpgsqlCommand("""
UPDATE families SET last_window_start_nanos = @last_window_start_nanos
WHERE run_id = @run_id AND family_id = @family_id
""", connection, transaction);
                update.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
                update.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
                update.Parameters.AddWithValue("last_window_start_nanos", NpgsqlDbType.Bigint, last);
                update.ExecuteNonQuery();
                return new FamilyInfoDto(familyId, familyKey, familyName, first, last);
            }
        }

        using var insert = new NpgsqlCommand("""
WITH next_id AS (
    SELECT COALESCE(MAX(family_id), 0) + 1 AS family_id
    FROM families
    WHERE run_id = @run_id
)
INSERT INTO families (run_id, family_id, key, name, first_window_start_nanos, last_window_start_nanos)
SELECT @run_id, family_id, @key, @name, @window_start_nanos, @window_start_nanos
FROM next_id
RETURNING family_id
""", connection, transaction);
        insert.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
        insert.Parameters.AddWithValue("key", NpgsqlDbType.Text, familyKey);
        insert.Parameters.AddWithValue("name", NpgsqlDbType.Text, familyName);
        insert.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, windowStart);
        var newFamilyId = (int)insert.ExecuteScalar()!;
        return new FamilyInfoDto(newFamilyId, familyKey, familyName, windowStart, windowStart);
    }

    private static void UpsertSummary(NpgsqlConnection connection, NpgsqlTransaction transaction, long runId, SliceSummaryDto summary)
    {
        using var command = new NpgsqlCommand("""
INSERT INTO slice_summaries (
    run_id, family_id, family_key, family_name, window_start_nanos, window_end_nanos, window_start_iso,
    document_count, interaction_count, max_semantic_weight, total_semantic_weight,
    predicate_counts, node_kind_counts, jaccard_node_kind, jaccard_previous_self, reduction, retention_level)
VALUES (
    @run_id, @family_id, @family_key, @family_name, @window_start_nanos, @window_end_nanos, @window_start_iso,
    @document_count, @interaction_count, @max_semantic_weight, @total_semantic_weight,
    @predicate_counts, @node_kind_counts, @jaccard_node_kind, @jaccard_previous_self, @reduction, @retention_level)
ON CONFLICT (run_id, family_id, window_start_nanos) DO UPDATE SET
    window_end_nanos = EXCLUDED.window_end_nanos,
    document_count = EXCLUDED.document_count,
    interaction_count = EXCLUDED.interaction_count,
    max_semantic_weight = EXCLUDED.max_semantic_weight,
    total_semantic_weight = EXCLUDED.total_semantic_weight,
    predicate_counts = EXCLUDED.predicate_counts,
    node_kind_counts = EXCLUDED.node_kind_counts,
    jaccard_node_kind = EXCLUDED.jaccard_node_kind,
    jaccard_previous_self = EXCLUDED.jaccard_previous_self,
    reduction = EXCLUDED.reduction,
    retention_level = EXCLUDED.retention_level
""", connection, transaction);

        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, summary.FamilyId);
        command.Parameters.AddWithValue("family_key", NpgsqlDbType.Text, summary.FamilyKey);
        command.Parameters.AddWithValue("family_name", NpgsqlDbType.Text, summary.FamilyName);
        command.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, summary.WindowStartNanos);
        command.Parameters.AddWithValue("window_end_nanos", NpgsqlDbType.Bigint, summary.WindowEndNanos);
        command.Parameters.AddWithValue("window_start_iso", NpgsqlDbType.Text, summary.WindowStartIso);
        command.Parameters.AddWithValue("document_count", NpgsqlDbType.Integer, summary.DocumentCount);
        command.Parameters.AddWithValue("interaction_count", NpgsqlDbType.Integer, summary.InteractionCount);
        command.Parameters.AddWithValue("max_semantic_weight", NpgsqlDbType.Double, summary.MaxSemanticWeight);
        command.Parameters.AddWithValue("total_semantic_weight", NpgsqlDbType.Double, summary.TotalSemanticWeight);
        command.Parameters.AddWithValue("predicate_counts", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary.PredicateCounts, JsonOptions));
        command.Parameters.AddWithValue("node_kind_counts", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary.NodeKindCounts, JsonOptions));
        command.Parameters.AddWithValue("jaccard_node_kind", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary.JaccardNodeKind, JsonOptions));
        command.Parameters.AddWithValue("jaccard_previous_self", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary.JaccardPreviousSelf, JsonOptions));
        command.Parameters.AddWithValue("reduction", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary.Reduction, JsonOptions));
        command.Parameters.AddWithValue("retention_level", NpgsqlDbType.Text, summary.RetentionLevel.ToString());
        command.ExecuteNonQuery();
    }

    private static void ReplaceSliceRows(NpgsqlConnection connection, NpgsqlTransaction transaction, long runId, int familyId, DisparityGraphSlice slice)
    {
        using (var deleteInteractions = new NpgsqlCommand("DELETE FROM backbone_interactions WHERE run_id = @run_id AND family_id = @family_id AND window_start_nanos = @window_start_nanos", connection, transaction))
        {
            deleteInteractions.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
            deleteInteractions.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
            deleteInteractions.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, slice.WindowStartNanos);
            deleteInteractions.ExecuteNonQuery();
        }

        using (var deleteDocuments = new NpgsqlCommand("DELETE FROM node_documents WHERE run_id = @run_id AND family_id = @family_id AND window_start_nanos = @window_start_nanos", connection, transaction))
        {
            deleteDocuments.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
            deleteDocuments.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
            deleteDocuments.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, slice.WindowStartNanos);
            deleteDocuments.ExecuteNonQuery();
        }

        using (var writer = connection.BeginBinaryImport("""
COPY node_documents (
    run_id, family_id, window_start_nanos, node_id, node_kind, jaccard_node_kind, jaccard_previous_self, term_counts, tfidf_weights)
FROM STDIN (FORMAT BINARY)
"""))
        {
            foreach (var document in slice.Documents)
            {
                writer.StartRow();
                writer.Write(runId, NpgsqlDbType.Bigint);
                writer.Write(familyId, NpgsqlDbType.Integer);
                writer.Write(slice.WindowStartNanos, NpgsqlDbType.Bigint);
                writer.Write(document.NodeId, NpgsqlDbType.Uuid);
                writer.Write(document.NodeKind, NpgsqlDbType.Text);
                WriteNullableDouble(writer, document.JaccardToNodeKindBaseline);
                WriteNullableDouble(writer, document.JaccardToPreviousSelf);
                writer.Write(JsonSerializer.Serialize(document.TermCounts, JsonOptions), NpgsqlDbType.Jsonb);
                writer.Write(JsonSerializer.Serialize(document.TfidfWeights, JsonOptions), NpgsqlDbType.Jsonb);
            }

            writer.Complete();
        }

        using (var writer = connection.BeginBinaryImport("""
COPY backbone_interactions (
    run_id, family_id, window_start_nanos, source_node_id, target_node_id, count, semantic_weight,
    predicate_counts, predicate_semantic_weights, term_counts, evidence,
    source_degree, source_strength, source_normalized_weight, source_significance, source_is_significant,
    target_degree, target_strength, target_normalized_weight, target_significance, target_is_significant)
FROM STDIN (FORMAT BINARY)
"""))
        {
            foreach (var interaction in slice.Interactions)
            {
                writer.StartRow();
                writer.Write(runId, NpgsqlDbType.Bigint);
                writer.Write(familyId, NpgsqlDbType.Integer);
                writer.Write(slice.WindowStartNanos, NpgsqlDbType.Bigint);
                writer.Write(interaction.SourceNodeId, NpgsqlDbType.Uuid);
                writer.Write(interaction.TargetNodeId, NpgsqlDbType.Uuid);
                writer.Write(interaction.Count, NpgsqlDbType.Integer);
                writer.Write(interaction.SemanticWeight, NpgsqlDbType.Double);
                writer.Write(JsonSerializer.Serialize(interaction.PredicateCounts, JsonOptions), NpgsqlDbType.Jsonb);
                writer.Write(JsonSerializer.Serialize(interaction.PredicateSemanticWeights, JsonOptions), NpgsqlDbType.Jsonb);
                writer.Write(JsonSerializer.Serialize(interaction.TermCounts, JsonOptions), NpgsqlDbType.Jsonb);
                writer.Write(JsonSerializer.Serialize(interaction.Evidence.Select(ToDto).ToList(), JsonOptions), NpgsqlDbType.Jsonb);
                WriteScore(writer, interaction.SourceOutgoing);
                WriteScore(writer, interaction.TargetIncoming);
            }

            writer.Complete();
        }
    }

    private SliceRetentionLevel? GetSummaryRetention(long runId, int familyId, long windowStart)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
SELECT retention_level
FROM slice_summaries
WHERE run_id = @run_id AND family_id = @family_id AND window_start_nanos = @window_start_nanos
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
        command.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, windowStart);
        var result = command.ExecuteScalar();
        return result is null ? null : Enum.Parse<SliceRetentionLevel>((string)result);
    }

    private static IReadOnlyList<ProjectedNodeDto> GetProjectedNodes(NpgsqlConnection connection, long runId, int familyId, long windowStart, HashSet<Guid> nodeIds)
    {
        if (nodeIds.Count == 0) return [];

        using var command = new NpgsqlCommand("""
SELECT node_id, node_kind, jaccard_node_kind, jaccard_previous_self
FROM node_documents
WHERE run_id = @run_id AND family_id = @family_id AND window_start_nanos = @window_start_nanos AND node_id = ANY(@node_ids)
ORDER BY node_id
""", connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Bigint, runId);
        command.Parameters.AddWithValue("family_id", NpgsqlDbType.Integer, familyId);
        command.Parameters.AddWithValue("window_start_nanos", NpgsqlDbType.Bigint, windowStart);
        command.Parameters.AddWithValue("node_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, nodeIds.ToArray());

        var nodes = new List<ProjectedNodeDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var nodeId = reader.GetGuid(0);
            nodes.Add(new ProjectedNodeDto(
                nodeId.ToString("D"),
                reader.GetString(1),
                nodeId.ToString("D")[..8],
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3)));
        }

        return nodes;
    }

    private static SliceSummaryDto ReadSummary(NpgsqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetInt64(4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetDouble(8),
        reader.GetDouble(9),
        Deserialize<Dictionary<string, int>>(reader.GetString(10)),
        Deserialize<Dictionary<string, int>>(reader.GetString(11)),
        Deserialize<JaccardDistributionDto>(reader.GetString(12)),
        Deserialize<JaccardDistributionDto>(reader.GetString(13)),
        Deserialize<DisparitySliceReduction>(reader.GetString(14)),
        Enum.Parse<SliceRetentionLevel>(reader.GetString(15)));

    private static SliceSummaryDto BuildSummary(int familyId, string familyKey, string familyName, DisparityGraphSlice slice, SliceRetentionLevel level)
    {
        var docCounts = slice.Documents.GroupBy(d => d.NodeKind).ToDictionary(g => g.Key, g => g.Count());
        var predCounts = slice.Interactions
            .SelectMany(interaction => interaction.PredicateCounts)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);

        return new SliceSummaryDto(
            familyId,
            familyKey,
            familyName,
            slice.WindowStartNanos,
            slice.WindowEndNanos,
            DateTimeOffset.UnixEpoch.AddTicks(slice.WindowStartNanos / 100).ToString("o"),
            slice.Documents.Count,
            slice.Interactions.Count,
            slice.Interactions.Count > 0 ? slice.Interactions.Max(i => i.SemanticWeight) : 0,
            slice.Interactions.Sum(i => i.SemanticWeight),
            predCounts,
            docCounts,
            BuildJaccard(slice.Documents.Select(d => d.JaccardToNodeKindBaseline)),
            BuildJaccard(slice.Documents.Select(d => d.JaccardToPreviousSelf)),
            slice.Reduction,
            level);
    }

    private static JaccardDistributionDto BuildJaccard(IEnumerable<double?> values)
    {
        var materialized = values.ToList();
        var valid = materialized.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        var nulls = materialized.Count - valid.Count;
        if (valid.Count == 0) return new JaccardDistributionDto(0, nulls, 0, 0, 0, 0, 0, 0);

        return new JaccardDistributionDto(
            valid.Count,
            nulls,
            valid.First(),
            valid.Last(),
            valid.Average(),
            valid[(int)(valid.Count * 0.25)],
            valid[(int)(valid.Count * 0.50)],
            valid[(int)(valid.Count * 0.75)]);
    }

    private StoreMemoryPressureDto BuildMemoryPressure(int summaries) => new(
        summaries,
        0,
        summaries,
        0,
        0,
        0,
        0,
        0,
        0,
        GC.GetTotalMemory(false),
        Environment.WorkingSet,
        GC.GetGCMemoryInfo().HeapSizeBytes);

    private void PublishRunStatus()
    {
        _eventHub.Publish(new ServerSentEvent("run-status", JsonSerializer.Serialize(GetRunStatus(), JsonOptions)));
    }

    private static IReadOnlySet<string> ReadStringSet(NpgsqlCommand command)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private static DirectionalDisparityScore ReadScore(NpgsqlDataReader reader, int offset) => new()
    {
        Degree = reader.GetInt32(offset),
        Strength = reader.GetDouble(offset + 1),
        NormalizedWeight = reader.GetDouble(offset + 2),
        Significance = reader.IsDBNull(offset + 3) ? null : reader.GetDouble(offset + 3),
        IsSignificant = reader.GetBoolean(offset + 4)
    };

    private static void WriteScore(NpgsqlBinaryImporter writer, DirectionalDisparityScore score)
    {
        writer.Write(score.Degree, NpgsqlDbType.Integer);
        writer.Write(score.Strength, NpgsqlDbType.Double);
        writer.Write(score.NormalizedWeight, NpgsqlDbType.Double);
        WriteNullableDouble(writer, score.Significance);
        writer.Write(score.IsSignificant, NpgsqlDbType.Boolean);
    }

    private static void WriteNullableDouble(NpgsqlBinaryImporter writer, double? value)
    {
        if (value.HasValue)
        {
            writer.Write(value.Value, NpgsqlDbType.Double);
        }
        else
        {
            writer.WriteNull();
        }
    }

    private static EvidencePointerDto ToDto(EvidencePointer evidence) =>
        new(evidence.Source.SegmentPath, evidence.Source.SyncBlockOffset, evidence.TimestampNanos, evidence.EventId?.ToString("D"));

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
}
