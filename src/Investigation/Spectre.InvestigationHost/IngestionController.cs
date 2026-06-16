using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.DisparityFiltering.Sinks;
using Spectre.InvestigationHost.Store;
using Spectre.SemanticIndexing.Sinks;

namespace Spectre.InvestigationHost;

public sealed class IngestionController
{
    private readonly IInvestigationRunStore _store;
    private readonly IInvestigationQueryService _queries;
    private readonly IConfiguration _config;
    private readonly ILogger<IngestionController> _logger;
    private readonly IIngestionStage _ingestionStage;
    private readonly IIndexingStage _indexingStage;
    private readonly IBackboneStage _backboneStage;
    private readonly object _lock = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public IngestionController(
        IInvestigationRunStore store,
        IInvestigationQueryService queries,
        IIngestionStage ingestionStage,
        IIndexingStage indexingStage,
        IBackboneStage backboneStage,
        IConfiguration config,
        ILogger<IngestionController> logger)
    {
        _store = store;
        _queries = queries;
        _ingestionStage = ingestionStage;
        _indexingStage = indexingStage;
        _backboneStage = backboneStage;
        _config = config;
        _logger = logger;
    }

    public PipelineRunControlResult Start(string? inputPath)
    {
        lock (_lock)
        {
            if (_runTask is { IsCompleted: false })
            {
                return new PipelineRunControlResult(false, "Ingestion is already running.", _queries.GetRunStatus());
            }

            var resolvedInputPath = string.IsNullOrWhiteSpace(inputPath)
                ? _config.GetValue<string>("InputPath") ?? "d:\\Proj\\data\\cadets"
                : inputPath;

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunIngestion(resolvedInputPath, _runCts.Token), CancellationToken.None);

            return new PipelineRunControlResult(true, $"Ingestion started from {resolvedInputPath}.", _queries.GetRunStatus());
        }
    }

    public PipelineRunControlResult Cancel()
    {
        lock (_lock)
        {
            if (_runTask is not { IsCompleted: false } || _runCts is null)
            {
                return new PipelineRunControlResult(false, "No ingestion run is active.", _queries.GetRunStatus());
            }

            _runCts.Cancel();
            return new PipelineRunControlResult(true, "Cancellation requested.", _queries.GetRunStatus());
        }
    }

    private void RunIngestion(string inputPath, CancellationToken ct)
    {
        var indexingOptions = new SemanticIndexingOptions();
        var defaultFilteringOptions = new DisparityFilterOptions();
        var filteringOptions = new DisparityFilterOptions
        {
            Alpha = _config.GetValue<double?>("DisparityFilter:Alpha") ?? defaultFilteringOptions.Alpha,
            MaxEvidencePointersPerEdge = _config.GetValue<int?>("DisparityFilter:MaxEvidencePointersPerEdge")
                ?? defaultFilteringOptions.MaxEvidencePointersPerEdge
        };
        SemanticIndexingGraphFactSink? indexingSink = null;
        DisparityFilteringSemanticGraphSliceSink? filteringSink = null;

        _logger.LogInformation("Starting ingestion from {InputPath}", inputPath);
        _store.MarkRunState(RunState.Running);

        try
        {
            var result = _ingestionStage.Run(
                new[] { inputPath },
                () =>
                {
                    var sinkAdapter = new DashboardSliceSinkAdapter(_store);
                    filteringSink = (DisparityFilteringSemanticGraphSliceSink)_backboneStage.CreateSink(sinkAdapter, filteringOptions);
                    indexingSink = (SemanticIndexingGraphFactSink)_indexingStage.CreateSink(filteringSink, indexingOptions);
                    return indexingSink;
                },
                ct);

            var finalState = result.Outcome switch
            {
                IngestionOutcome.Completed => RunState.Completed,
                IngestionOutcome.Canceled => RunState.Canceled,
                IngestionOutcome.Failed => RunState.Failed,
                _ => RunState.Failed
            };

            var isPartial = result.Outcome != IngestionOutcome.Completed;

            if (result.Exception is not null)
            {
                _logger.LogError(result.Exception, "Ingestion runner returned an exception.");
            }

            _store.MarkRunState(
                finalState,
                isPartial: isPartial,
                indexingMetrics: indexingSink?.Metrics,
                filteringMetrics: filteringSink?.Metrics);
            _logger.LogInformation("Ingestion finished with outcome {Outcome}.", result.Outcome);
        }
        catch (OperationCanceledException)
        {
            _store.MarkRunState(RunState.Canceled, isPartial: true);
            _logger.LogWarning("Ingestion canceled.");
        }
        catch (Exception ex)
        {
            _store.MarkRunState(RunState.Failed, isPartial: true);
            _logger.LogError(ex, "Ingestion failed with exception.");
        }
    }
}
