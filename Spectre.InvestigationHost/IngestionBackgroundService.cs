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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run on background thread so Kestrel is not blocked
        _ = Task.Run(() => RunIngestion(stoppingToken), stoppingToken);
        await Task.CompletedTask;
    }

    private void RunIngestion(CancellationToken ct)
    {
        var inputPath = _config.GetValue<string>("InputPath") ?? "d:\\Proj\\data\\cadets";
        var options = new SemanticIndexingOptions(); // Defaults
        
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
                    return new SemanticIndexingGraphFactSink(sinkAdapter, options);
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
            
            _store.MarkRunState(finalState, isPartial: isPartial);
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
