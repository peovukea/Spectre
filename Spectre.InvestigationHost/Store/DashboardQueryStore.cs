using System.Diagnostics;
using System.Text.Json;
using Spectre.SemanticIndexing;

namespace Spectre.InvestigationHost.Store;

public sealed class DashboardQueryStore
{
    private readonly EventHub _eventHub;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SemaphoreSlim _expensiveQuerySemaphore = new(4, 4);

    public int MaxDetailedSlices { get; init; } = 50;
    public long MaxDetailedBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxProjectionSlices { get; init; } = 100;
    public long MaxProjectionBytes { get; init; } = 256L * 1024 * 1024;
    public int DefaultMaxNodes { get; init; } = 250;
    public int DefaultMaxEdges { get; init; } = 200;
    /// <summary>
    /// Optional process-wide emergency threshold. Disabled by default because
    /// active ingestion/indexing memory cannot be reclaimed by this store.
    /// </summary>
    public long EmergencyProcessMemoryLimit { get; init; }

    private readonly Dictionary<string, FamilyInfoDto> _families = [];
    private readonly List<SliceSummaryDto> _summaries = [];
    private readonly LinkedList<BoundedProjection> _projections = [];
    private readonly LinkedList<DetailedSliceEntry> _detailed = [];
    private readonly HashSet<string> _predicates = [];
    private readonly HashSet<string> _nodeKinds = [];

    private RunState _runState = RunState.NotStarted;
    private bool _isPartial = false;
    private SemanticIndexingMetrics? _metricsSnapshot;
    private readonly Stopwatch _runTimer = new();

    private long _estimatedDetailedBytes;
    private long _estimatedProjectionBytes;
    private int _evictedDetailedSlices;
    private int _evictedProjections;

    public DashboardQueryStore(EventHub eventHub)
    {
        _eventHub = eventHub;
    }

    public void MarkRunState(RunState state, bool isPartial = false, SemanticIndexingMetrics? metrics = null)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_runState == RunState.NotStarted && state == RunState.Running)
                _runTimer.Start();
            else if (state is RunState.Completed or RunState.Failed or RunState.Canceled)
                _runTimer.Stop();

            _runState = state;
            _isPartial |= isPartial;
            if (metrics != null) _metricsSnapshot = metrics;
            PublishRunStatus();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void MarkWritesClosed()
    {
        // Handled by run state transition externally, but can trigger final flush
    }

    public void AcceptSlice(SemanticGraphSlice slice)
    {
        SliceSummaryDto summary;
        long detailedBytes = EstimateDetailedBytes(slice);
        long projectionBytes = EstimateProjectionBytes(slice);
        List<ServerSentEvent> eventsToPublish = [];

        _lock.EnterWriteLock();
        try
        {
            _metricsSnapshot = slice.Metrics;
            
            // 1. Resolve family
            var basePath = slice.InputFamilyBasePath ?? "unknown";
            var (familyKey, familyName) = FamilyKeyResolver.Resolve(basePath);
            if (!_families.TryGetValue(familyKey, out var family))
            {
                family = new FamilyInfoDto(_families.Count + 1, familyKey, familyName, slice.WindowStartNanos, slice.WindowEndNanos);
                _families[familyKey] = family;
            }
            else
            {
                _families[familyKey] = family with { LastWindowStartNanos = Math.Max(family.LastWindowStartNanos, slice.WindowEndNanos) };
            }

            // 2. Accumulate predicates and node kinds
            foreach (var doc in slice.Documents) _nodeKinds.Add(doc.NodeKind);
            foreach (var inter in slice.Interactions) _predicates.Add(inter.Predicate);

            // 3. Evaluate limits and memory
            CheckEmergencyEviction(eventsToPublish);

            // Keep every individually admissible new slice, then evict the oldest
            // entries below. This makes retention a rolling window instead of
            // permanently retaining the first entries that filled each tier.
            bool keepDetailed = MaxDetailedSlices > 0 && detailedBytes <= MaxDetailedBytes;
            bool keepProjection = MaxProjectionSlices > 0 && projectionBytes <= MaxProjectionBytes;

            var level = keepDetailed ? SliceRetentionLevel.Detailed 
                      : keepProjection ? SliceRetentionLevel.Projection 
                      : SliceRetentionLevel.Summary;

            // Build summary
            summary = BuildSummary(family.Id, familyKey, familyName, slice, level);
            _summaries.Add(summary);

            if (keepProjection)
            {
                var proj = BuildDefaultProjection(family.Id, slice, projectionBytes);
                _projections.AddLast(proj);
                _estimatedProjectionBytes += projectionBytes;
            }

            if (keepDetailed)
            {
                _detailed.AddLast(new DetailedSliceEntry(family.Id, slice.WindowStartNanos, slice, detailedBytes));
                _estimatedDetailedBytes += detailedBytes;
            }

            EnforceLimits(eventsToPublish);

            eventsToPublish.Add(new ServerSentEvent("slice-closed", JsonSerializer.Serialize(summary, JsonOptions)));
            eventsToPublish.Add(new ServerSentEvent("memory-pressure", JsonSerializer.Serialize(BuildMemoryPressure(), JsonOptions)));
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        foreach (var ev in eventsToPublish) _eventHub.Publish(ev);
    }

    private void CheckEmergencyEviction(List<ServerSentEvent> events)
    {
        var ws = Environment.WorkingSet;
        if (EmergencyProcessMemoryLimit > 0 && ws > EmergencyProcessMemoryLimit)
        {
            while (_detailed.Count > 0) EvictOldestDetailed(events);
            while (_projections.Count > 0) EvictOldestProjection(events);
        }
    }

    private void EnforceLimits(List<ServerSentEvent> events)
    {
        while (_detailed.Count > MaxDetailedSlices || _estimatedDetailedBytes > MaxDetailedBytes)
        {
            if (_detailed.Count == 0) break;
            EvictOldestDetailed(events);
        }

        while (_projections.Count > MaxProjectionSlices || _estimatedProjectionBytes > MaxProjectionBytes)
        {
            if (_projections.Count == 0) break;
            EvictOldestProjection(events);
        }
    }

    private void EvictOldestDetailed(List<ServerSentEvent> events)
    {
        var oldest = _detailed.First!.Value;
        _detailed.RemoveFirst();
        _estimatedDetailedBytes -= oldest.EstimatedBytes;
        _evictedDetailedSlices++;

        UpdateSummaryRetention(oldest.FamilyId, oldest.WindowStartNanos, SliceRetentionLevel.Projection, events);
    }

    private void EvictOldestProjection(List<ServerSentEvent> events)
    {
        var oldest = _projections.First!.Value;
        _projections.RemoveFirst();
        _estimatedProjectionBytes -= oldest.EstimatedBytes;
        _evictedProjections++;

        UpdateSummaryRetention(oldest.FamilyId, oldest.WindowStartNanos, SliceRetentionLevel.Summary, events);
    }

    private void UpdateSummaryRetention(int familyId, long windowStart, SliceRetentionLevel newLevel, List<ServerSentEvent> events)
    {
        for (int i = 0; i < _summaries.Count; i++)
        {
            if (_summaries[i].FamilyId == familyId && _summaries[i].WindowStartNanos == windowStart)
            {
                if ((int)_summaries[i].RetentionLevel > (int)newLevel)
                {
                    _summaries[i] = _summaries[i] with { RetentionLevel = newLevel };
                    events.Add(new ServerSentEvent("retention-changed", JsonSerializer.Serialize(new
                    {
                        familyId,
                        windowStartNanos = windowStart,
                        newLevel = newLevel.ToString()
                    }, JsonOptions)));
                }
                break;
            }
        }
    }

    private StoreMemoryPressureDto BuildMemoryPressure()
    {
        return new StoreMemoryPressureDto(
            _detailed.Count,
            _projections.Count,
            _summaries.Count,
            _estimatedDetailedBytes,
            _estimatedProjectionBytes,
            MaxDetailedBytes,
            MaxProjectionBytes,
            _evictedDetailedSlices,
            _evictedProjections,
            GC.GetTotalMemory(false),
            Environment.WorkingSet,
            GC.GetGCMemoryInfo().HeapSizeBytes
        );
    }

    private void PublishRunStatus()
    {
        _eventHub.Publish(new ServerSentEvent("run-status", JsonSerializer.Serialize(new RunStatusDto(
            _runState,
            (long)_runTimer.Elapsed.TotalSeconds,
            _isPartial,
            _metricsSnapshot?.Snapshot()
        ), JsonOptions)));
    }

    public RunStatusDto GetRunStatus()
    {
        _lock.EnterReadLock();
        try
        {
            return new RunStatusDto(_runState, (long)_runTimer.Elapsed.TotalSeconds, _isPartial, _metricsSnapshot?.Snapshot());
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public StoreMemoryPressureDto GetMemoryPressure()
    {
        _lock.EnterReadLock();
        try
        {
            return BuildMemoryPressure();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<FamilyInfoDto> GetFamilies()
    {
        _lock.EnterReadLock();
        try { return _families.Values.ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<SliceSummaryDto> GetWindows(int familyId)
    {
        _lock.EnterReadLock();
        try { return _summaries.Where(s => s.FamilyId == familyId).ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlySet<string> GetPredicates()
    {
        _lock.EnterReadLock();
        try { return new HashSet<string>(_predicates); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlySet<string> GetNodeKinds()
    {
        _lock.EnterReadLock();
        try { return new HashSet<string>(_nodeKinds); }
        finally { _lock.ExitReadLock(); }
    }

    public GraphProjectionDto? GetProjection(int familyId, long windowStart, GraphQueryParameters p)
    {
        bool isDefault = p.MinWeight == 0.0 && p.Predicate == null && p.NodeKind == null && 
                         p.MaxNodes == DefaultMaxNodes && p.MaxEdges == DefaultMaxEdges;

        SemanticGraphSlice? detailedSlice = null;

        _lock.EnterReadLock();
        try
        {
            var summary = _summaries.FirstOrDefault(s => s.FamilyId == familyId && s.WindowStartNanos == windowStart);
            if (summary == null) return null;

            if (summary.RetentionLevel == SliceRetentionLevel.Summary)
                throw new InvalidOperationException("410 Gone");

            if (summary.RetentionLevel == SliceRetentionLevel.Projection)
            {
                if (!isDefault) throw new InvalidOperationException("410 Gone");
                var proj = _projections.FirstOrDefault(x => x.FamilyId == familyId && x.WindowStartNanos == windowStart);
                if (proj != null)
                {
                    return new GraphProjectionDto(proj.Nodes, proj.Edges, proj.Truncated, proj.TotalMatchingInteractions, DefaultMaxNodes, DefaultMaxEdges, SliceRetentionLevel.Projection);
                }
            }

            if (summary.RetentionLevel == SliceRetentionLevel.Detailed)
            {
                detailedSlice = _detailed.FirstOrDefault(x => x.FamilyId == familyId && x.WindowStartNanos == windowStart)?.Slice;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (detailedSlice == null) throw new InvalidOperationException("410 Gone");

        // Expensive query outside lock
        _expensiveQuerySemaphore.Wait();
        try
        {
            return BuildProjectionFromSlice(detailedSlice, p);
        }
        finally
        {
            _expensiveQuerySemaphore.Release();
        }
    }

    public NodeDetailDto? GetNodeDetail(int familyId, long windowStart, Guid nodeId)
    {
        SemanticGraphSlice? detailedSlice = null;
        _lock.EnterReadLock();
        try
        {
            detailedSlice = _detailed.FirstOrDefault(x => x.FamilyId == familyId && x.WindowStartNanos == windowStart)?.Slice;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (detailedSlice == null) throw new InvalidOperationException("410 Gone");

        var doc = detailedSlice.Documents.FirstOrDefault(d => d.NodeId == nodeId);
        if (doc == null) return null;

        return new NodeDetailDto(
            doc.NodeId.ToString("D"), doc.NodeKind, doc.NodeId.ToString("D")[..8],
            doc.JaccardToNodeKindBaseline, doc.JaccardToPreviousSelf,
            new Dictionary<string, int>(doc.TermCounts),
            new Dictionary<string, double>(doc.TfidfWeights));
    }

    public InteractionDetailDto? GetInteractionDetail(int familyId, long windowStart, Guid source, Guid target, string predicate)
    {
        SemanticGraphSlice? detailedSlice = null;
        _lock.EnterReadLock();
        try
        {
            detailedSlice = _detailed.FirstOrDefault(x => x.FamilyId == familyId && x.WindowStartNanos == windowStart)?.Slice;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (detailedSlice == null) throw new InvalidOperationException("410 Gone");

        var inter = detailedSlice.Interactions.FirstOrDefault(i => i.SourceNodeId == source && i.TargetNodeId == target && i.Predicate == predicate);
        if (inter == null) return null;

        return new InteractionDetailDto(
            inter.SourceNodeId.ToString("D"), inter.TargetNodeId.ToString("D"), inter.Predicate,
            inter.Count, inter.SemanticWeight,
            new Dictionary<string, int>(inter.TermCounts),
            inter.Evidence.Select(e => new EvidencePointerDto(e.Source.SegmentPath, e.Source.SyncBlockOffset, e.TimestampNanos, e.EventId?.ToString("D"))).ToList());
    }

    private static GraphProjectionDto BuildProjectionFromSlice(SemanticGraphSlice slice, GraphQueryParameters p)
    {
        // 1. Filter
        var filtered = slice.Interactions.Where(i => i.SemanticWeight >= p.MinWeight);
        if (p.Predicate != null)
        {
            filtered = filtered.Where(i => i.Predicate == p.Predicate);
        }

        var docKinds = slice.Documents.ToDictionary(d => d.NodeId, d => d.NodeKind);
        if (p.NodeKind != null)
        {
            filtered = filtered.Where(i => 
                (docKinds.TryGetValue(i.SourceNodeId, out var sk) && sk == p.NodeKind) ||
                (docKinds.TryGetValue(i.TargetNodeId, out var tk) && tk == p.NodeKind)
            );
        }

        // 2. Sort
        var sorted = filtered.OrderByDescending(i => i.SemanticWeight)
            .ThenBy(i => i.SourceNodeId)
            .ThenBy(i => i.TargetNodeId)
            .ThenBy(i => i.Predicate)
            .ToList();

        // 3. Walk and bound
        var acceptedEdges = new List<ProjectedEdgeDto>();
        var acceptedNodeIds = new HashSet<Guid>();
        bool truncated = false;

        foreach (var inter in sorted)
        {
            int extraNodes = 0;
            if (!acceptedNodeIds.Contains(inter.SourceNodeId)) extraNodes++;
            if (!acceptedNodeIds.Contains(inter.TargetNodeId)) extraNodes++;

            if (acceptedNodeIds.Count + extraNodes <= p.MaxNodes && acceptedEdges.Count < p.MaxEdges)
            {
                acceptedNodeIds.Add(inter.SourceNodeId);
                acceptedNodeIds.Add(inter.TargetNodeId);
                acceptedEdges.Add(new ProjectedEdgeDto(
                    inter.SourceNodeId.ToString("D"), inter.TargetNodeId.ToString("D"),
                    inter.Predicate, inter.Count, inter.SemanticWeight));
            }
            else
            {
                truncated = true;
            }
        }

        var acceptedNodes = slice.Documents
            .Where(d => acceptedNodeIds.Contains(d.NodeId))
            .Select(d => new ProjectedNodeDto(
                d.NodeId.ToString("D"), d.NodeKind, d.NodeId.ToString("D")[..8],
                d.JaccardToNodeKindBaseline, d.JaccardToPreviousSelf))
            .ToList();

        return new GraphProjectionDto(acceptedNodes, acceptedEdges, truncated, sorted.Count, p.MaxNodes, p.MaxEdges, SliceRetentionLevel.Detailed);
    }

    private BoundedProjection BuildDefaultProjection(int familyId, SemanticGraphSlice slice, long bytes)
    {
        var proj = BuildProjectionFromSlice(slice, new GraphQueryParameters { MaxNodes = DefaultMaxNodes, MaxEdges = DefaultMaxEdges });
        return new BoundedProjection(familyId, slice.WindowStartNanos, proj.Nodes, proj.Edges, proj.Truncated, proj.TotalMatchingInteractions, bytes);
    }

    private static SliceSummaryDto BuildSummary(int familyId, string familyKey, string familyName, SemanticGraphSlice slice, SliceRetentionLevel level)
    {
        var docCounts = slice.Documents.GroupBy(d => d.NodeKind).ToDictionary(g => g.Key, g => g.Count());
        var predCounts = slice.Interactions.GroupBy(i => i.Predicate).ToDictionary(g => g.Key, g => g.Count());
        
        return new SliceSummaryDto(
            familyId, familyKey, familyName,
            slice.WindowStartNanos, slice.WindowEndNanos,
            DateTimeOffset.UnixEpoch.AddTicks(slice.WindowStartNanos / 100).ToString("o"),
            slice.Documents.Count, slice.Interactions.Count,
            slice.Interactions.Count > 0 ? slice.Interactions.Max(i => i.SemanticWeight) : 0,
            slice.Interactions.Sum(i => i.SemanticWeight),
            predCounts, docCounts,
            BuildJaccard(slice.Documents.Select(d => d.JaccardToNodeKindBaseline)),
            BuildJaccard(slice.Documents.Select(d => d.JaccardToPreviousSelf)),
            level);
    }

    private static JaccardDistributionDto BuildJaccard(IEnumerable<double?> values)
    {
        var valid = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        int nulls = values.Count() - valid.Count;
        if (valid.Count == 0) return new JaccardDistributionDto(0, nulls, 0, 0, 0, 0, 0, 0);

        return new JaccardDistributionDto(
            valid.Count, nulls,
            valid.First(), valid.Last(), valid.Average(),
            valid[(int)(valid.Count * 0.25)], valid[(int)(valid.Count * 0.50)], valid[(int)(valid.Count * 0.75)]
        );
    }

    private static long EstimateDetailedBytes(SemanticGraphSlice slice)
    {
        long bytes = 0;
        foreach (var doc in slice.Documents)
        {
            bytes += 80; // struct
            bytes += 24 + doc.NodeKind.Length * 2;
            bytes += doc.TermCounts.Count * 80;
            bytes += doc.TfidfWeights.Count * 88;
        }
        foreach (var inter in slice.Interactions)
        {
            bytes += 100;
            bytes += 24 + inter.Predicate.Length * 2;
            bytes += inter.TermCounts.Count * 80;
            foreach (var ev in inter.Evidence)
            {
                bytes += 120 + 24 + ev.Source.SegmentPath.Length * 2;
            }
        }
        return bytes;
    }

    private static long EstimateProjectionBytes(SemanticGraphSlice slice)
    {
        // simplistic bound for projection, it's typically capped anyway
        return 250 * 100 + 500 * 150;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record DetailedSliceEntry(int FamilyId, long WindowStartNanos, SemanticGraphSlice Slice, long EstimatedBytes);
    private sealed record BoundedProjection(int FamilyId, long WindowStartNanos, IReadOnlyList<ProjectedNodeDto> Nodes, IReadOnlyList<ProjectedEdgeDto> Edges, bool Truncated, int TotalMatchingInteractions, long EstimatedBytes);
}
