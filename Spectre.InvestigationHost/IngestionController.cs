using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Pipeline;
using Spectre.CdmIngestion.Projection;
using Spectre.CdmIngestion.Readers;
using Spectre.DisparityFiltering;
using Spectre.DisparityFiltering.Sinks;
using Spectre.InvestigationHost.Store;
using Spectre.SemanticIndexing;
using Spectre.SemanticIndexing.Sinks;

namespace Spectre.InvestigationHost;

public sealed record StartIngestionRequest(string? InputPath);

public sealed record IngestionControlResult(bool Accepted, string Message, RunStatusDto Status);

public sealed class IngestionController
{
    private readonly IInvestigationStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<IngestionController> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public IngestionController(IInvestigationStore store, IConfiguration config, ILogger<IngestionController> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    public IngestionControlResult Start(string? inputPath)
    {
        lock (_lock)
        {
            if (_runTask is { IsCompleted: false })
            {
                return new IngestionControlResult(false, "Ingestion is already running.", _store.GetRunStatus());
            }

            var resolvedInputPath = string.IsNullOrWhiteSpace(inputPath)
                ? _config.GetValue<string>("InputPath") ?? "d:\\Proj\\data\\cadets"
                : inputPath;

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunIngestion(resolvedInputPath, _runCts.Token), CancellationToken.None);

            return new IngestionControlResult(true, $"Ingestion started from {resolvedInputPath}.", _store.GetRunStatus());
        }
    }

    public IngestionControlResult Cancel()
    {
        lock (_lock)
        {
            if (_runTask is not { IsCompleted: false } || _runCts is null)
            {
                return new IngestionControlResult(false, "No ingestion run is active.", _store.GetRunStatus());
            }

            _runCts.Cancel();
            return new IngestionControlResult(true, "Cancellation requested.", _store.GetRunStatus());
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
            var reader = new AvroReader(msg => _logger.LogWarning("{Msg}", msg));
            var projector = new GraphFactProjector();
            var pipeline = new IngestionPipeline(reader, projector);
            var runner = new CdmIngestionRunner(pipeline);

            var result = runner.Run(
                new[] { inputPath },
                () =>
                {
                    var sinkAdapter = new DashboardSliceSinkAdapter(_store);
                    filteringSink = new DisparityFilteringSemanticGraphSliceSink(sinkAdapter, filteringOptions);
                    indexingSink = new SemanticIndexingGraphFactSink(filteringSink, indexingOptions);
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
