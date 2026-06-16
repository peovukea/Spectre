using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spectre.CdmIngestion.Pipeline;
using Spectre.CdmIngestion.Projection;
using Spectre.CdmIngestion.Readers;
using Spectre.CdmIngestion.Sinks;
using Spectre.SemanticIndexing.Sinks;
using Spectre.InvestigationHost.Store;
using Spectre.SemanticIndexing;
using Spectre.CdmIngestion;
using Spectre.DisparityFiltering;
using Spectre.DisparityFiltering.Sinks;

namespace Spectre.InvestigationHost;

public sealed class IngestionBackgroundService : BackgroundService
{
    private readonly DashboardQueryStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<IngestionBackgroundService> _logger;

    public IngestionBackgroundService(DashboardQueryStore store, IConfiguration config, ILogger<IngestionBackgroundService> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(() => RunIngestion(stoppingToken), CancellationToken.None);
    }

    private void RunIngestion(CancellationToken ct)
    {
        var inputPath = _config.GetValue<string>("InputPath") ?? "d:\\Proj\\data\\cadets";
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

            bool isPartial = result.Outcome != IngestionOutcome.Completed;

            if (result.Exception != null)
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
