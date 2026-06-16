using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Spectre.CdmIngestion.Pipeline;
using Spectre.CdmIngestion.Projection;
using Spectre.CdmIngestion.Readers;
using Spectre.DisparityFiltering;
using Spectre.InvestigationHost.Data;
using Spectre.InvestigationHost;
using Spectre.SemanticIndexing;
using Spectre.InvestigationHost.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var investigationStoreConnectionString = builder.Configuration.GetConnectionString("InvestigationStore")
    ?? throw new InvalidOperationException("ConnectionStrings:InvestigationStore is required.");

builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(investigationStoreConnectionString).Build());
builder.Services.AddDbContext<InvestigationDbContext>(options =>
    options.UseNpgsql(investigationStoreConnectionString, npgsql => npgsql.SetPostgresVersion(17, 0)));
builder.Services.AddSingleton<IInvestigationRunStore, PostgresInvestigationStore>();
builder.Services.AddSingleton<IInvestigationQueryService>(services => (PostgresInvestigationStore)services.GetRequiredService<IInvestigationRunStore>());
builder.Services.AddSingleton<IIngestionStage>(_ =>
{
    var reader = new AvroReader(_2 => { });
    var projector = new GraphFactProjector();
    var pipeline = new IngestionPipeline(reader, projector);
    return new CdmIngestionRunner(pipeline);
});
builder.Services.AddSingleton<IIndexingStage, SemanticIndexingStage>();
builder.Services.AddSingleton<IBackboneStage, BackboneStage>();
builder.Services.AddSingleton<IngestionController>();
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddOpenApi("v1", options =>
{
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new LongAsStringJsonConverter());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

if (builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<InvestigationDbContext>();
    db.Database.Migrate();
}

app.Services.GetRequiredService<IInvestigationRunStore>().RecoverInterruptedRuns();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

// Ensure metadata endpoints don't cache during active run
void ApplyNoStore(HttpContext ctx, IInvestigationQueryService store)
{
    if (store.GetRunStatus().State == RunState.Running)
    {
        ctx.Response.Headers.CacheControl = "no-store";
    }
}

var legacyApi = app.MapGroup("/api");
var v1Api = app.MapGroup("/api/v1");

MapStreamingEndpoints(legacyApi);
MapStreamingEndpoints(v1Api);
MapPipelineEndpoints(legacyApi);
MapPipelineEndpoints(v1Api);
MapInvestigationEndpoints(legacyApi);
MapInvestigationEndpoints(v1Api);

app.Run();

void MapStreamingEndpoints(RouteGroupBuilder api)
{
    api.MapGet("/events", async (HttpContext ctx, EventHub hub, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    
    _ = ctx.Request.Headers["Last-Event-ID"].ToString();

    var enumerator = hub.Subscribe(ct).GetAsyncEnumerator(ct);
    try
    {
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));

        var moveNext = enumerator.MoveNextAsync().AsTask();
        var nextHeartbeat = heartbeat.WaitForNextTickAsync(ct).AsTask();

        while (!ct.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(moveNext, nextHeartbeat);
            if (completed == nextHeartbeat)
            {
                if (!await nextHeartbeat)
                {
                    break;
                }

                await ctx.Response.WriteAsync(": heartbeat\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                nextHeartbeat = heartbeat.WaitForNextTickAsync(ct).AsTask();
                continue;
            }

            if (!await moveNext)
            {
                break;
            }

            var sse = enumerator.Current;
            await ctx.Response.WriteAsync($"id: {sse.Id}\nevent: {sse.EventType}\ndata: {sse.Data}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            moveNext = enumerator.MoveNextAsync().AsTask();
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Browser EventSource reconnects and disconnects are normal for SSE.
    }
    catch (IOException) when (ct.IsCancellationRequested)
    {
        // The response body can close while a heartbeat or event write is in flight.
    }
    finally
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (NotSupportedException)
        {
            // Some ChannelReader async enumerators do not support async disposal.
        }
    }
}).WithTags("Pipeline");
}

void MapPipelineEndpoints(RouteGroupBuilder api)
{
    api.MapGet("/runs", (IInvestigationQueryService store) => Results.Ok(store.GetRuns()))
        .WithTags("Pipeline", "Investigation");

    api.MapGet("/status", (long? runId, IInvestigationQueryService store) => Results.Ok(store.GetRunStatus(runId)))
        .WithTags("Pipeline");

    api.MapPost("/pipeline/start", (StartPipelineRunRequest? request, IngestionController ingestion) =>
    {
        var result = ingestion.Start(request?.InputPath);
        return result.Accepted ? Results.Accepted("/status", result) : Results.Conflict(result);
    }).WithTags("Pipeline", "Ingestion");

    api.MapPost("/pipeline/cancel", (IngestionController ingestion) =>
    {
        var result = ingestion.Cancel();
        return result.Accepted ? Results.Accepted("/status", result) : Results.Conflict(result);
    }).WithTags("Pipeline", "Ingestion");

    api.MapPost("/ingestion/start", (StartPipelineRunRequest? request, IngestionController ingestion) =>
    {
        var result = ingestion.Start(request?.InputPath);
        return result.Accepted ? Results.Accepted("/status", result) : Results.Conflict(result);
    }).WithTags("Pipeline", "Ingestion");

    api.MapPost("/ingestion/cancel", (IngestionController ingestion) =>
    {
        var result = ingestion.Cancel();
        return result.Accepted ? Results.Accepted("/status", result) : Results.Conflict(result);
    }).WithTags("Pipeline", "Ingestion");

    api.MapGet("/ingestion/status", (IInvestigationQueryService store, long? runId) => Results.Ok(store.GetRunStatus(runId)))
        .WithTags("Ingestion");

    api.MapGet("/indexing/status", (IInvestigationQueryService store, long? runId) => Results.Ok(store.GetRunStatus(runId)))
        .WithTags("Indexing");

    api.MapGet("/backbone/status", (IInvestigationQueryService store, long? runId) => Results.Ok(store.GetRunStatus(runId)))
        .WithTags("Backbone");
}

void MapInvestigationEndpoints(RouteGroupBuilder api)
{
    api.MapGet("/memory", (IInvestigationQueryService store) => Results.Ok(store.GetMemoryPressure()))
        .WithTags("Investigation");

    api.MapGet("/families", (HttpContext ctx, long? runId, IInvestigationQueryService store) =>
    {
        ApplyNoStore(ctx, store);
        return Results.Ok(store.GetFamilies(runId));
    }).WithTags("Investigation");

    api.MapGet("/predicates", (HttpContext ctx, long? runId, IInvestigationQueryService store) =>
    {
        ApplyNoStore(ctx, store);
        return Results.Ok(store.GetPredicates(runId));
    }).WithTags("Investigation");

    api.MapGet("/node-kinds", (HttpContext ctx, long? runId, IInvestigationQueryService store) =>
    {
        ApplyNoStore(ctx, store);
        return Results.Ok(store.GetNodeKinds(runId));
    }).WithTags("Investigation");

    api.MapGet("/families/{familyId:int}/windows", (HttpContext ctx, int familyId, long? runId, IInvestigationQueryService store) =>
    {
        ApplyNoStore(ctx, store);
        return Results.Ok(store.GetWindows(familyId, runId));
    }).WithTags("Investigation");

    api.MapGet("/families/{familyId:int}/windows/{windowStart:long}/graph", (int familyId, long windowStart, long? runId, double? minWeight, int? maxNodes, int? maxEdges, string? predicate, string? nodeKind, IInvestigationQueryService store) =>
    {
        var p = new GraphQueryParameters(
            minWeight ?? 0.0,
            maxNodes ?? store.DefaultMaxNodes,
            maxEdges ?? store.DefaultMaxEdges,
            predicate,
            nodeKind);

        var validation = ValidateGraphQuery(p, store, runId);
        if (validation is not null)
        {
            return validation;
        }

        return ToResult(store.GetProjection(familyId, windowStart, p, runId));
    }).WithTags("Investigation");

    api.MapGet("/families/{familyId:int}/windows/{windowStart:long}/nodes/{nodeId:guid}", (int familyId, long windowStart, Guid nodeId, long? runId, IInvestigationQueryService store) =>
        ToResult(store.GetNodeDetail(familyId, windowStart, nodeId, runId))).WithTags("Investigation");

    api.MapGet("/families/{familyId:int}/windows/{windowStart:long}/interactions/{source:guid}/{target:guid}", (int familyId, long windowStart, Guid source, Guid target, long? runId, IInvestigationQueryService store) =>
        ToResult(store.GetInteractionDetail(familyId, windowStart, source, target, runId))).WithTags("Investigation");
}

static IResult? ValidateGraphQuery(GraphQueryParameters parameters, IInvestigationQueryService store, long? runId)
{
    var validation = GraphQueryValidator.Validate(parameters, store.GetPredicates(runId), store.GetNodeKinds(runId));
    if (!validation.IsValid)
    {
        return Results.BadRequest(new { error = validation.Error });
    }

    return null;
}

static IResult ToResult<T>(StoreQueryResult<T> result) =>
    result.Status switch
    {
        StoreQueryStatus.Found => Results.Ok(result.Value),
        StoreQueryStatus.NotFound => Results.NotFound(),
        StoreQueryStatus.Gone => Results.StatusCode(StatusCodes.Status410Gone),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
