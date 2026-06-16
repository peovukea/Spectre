using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Spectre.InvestigationHost.Data;
using Spectre.InvestigationHost;
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
builder.Services.AddSingleton<IInvestigationStore, PostgresInvestigationStore>();
builder.Services.AddSingleton<IngestionController>();
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
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

app.Services.GetRequiredService<IInvestigationStore>().RecoverInterruptedRuns();

app.UseCors();

// Ensure metadata endpoints don't cache during active run
void ApplyNoStore(HttpContext ctx, IInvestigationStore store)
{
    if (store.GetRunStatus().State == RunState.Running)
    {
        ctx.Response.Headers.CacheControl = "no-store";
    }
}

app.MapGet("/api/events", async (HttpContext ctx, EventHub hub, CancellationToken ct) =>
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
});

app.MapGet("/api/runs", (IInvestigationStore store) =>
{
    return Results.Ok(store.GetRuns());
});

app.MapGet("/api/status", (long? runId, IInvestigationStore store) =>
{
    return Results.Ok(store.GetRunStatus(runId));
});

app.MapPost("/api/ingestion/start", (StartIngestionRequest? request, IngestionController ingestion) =>
{
    var result = ingestion.Start(request?.InputPath);
    return result.Accepted ? Results.Accepted("/api/status", result) : Results.Conflict(result);
});

app.MapPost("/api/ingestion/cancel", (IngestionController ingestion) =>
{
    var result = ingestion.Cancel();
    return result.Accepted ? Results.Accepted("/api/status", result) : Results.Conflict(result);
});

app.MapGet("/api/memory", (IInvestigationStore store) =>
{
    return Results.Ok(store.GetMemoryPressure());
});

app.MapGet("/api/families", (HttpContext ctx, long? runId, IInvestigationStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetFamilies(runId));
});

app.MapGet("/api/predicates", (HttpContext ctx, long? runId, IInvestigationStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetPredicates(runId));
});

app.MapGet("/api/node-kinds", (HttpContext ctx, long? runId, IInvestigationStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetNodeKinds(runId));
});

app.MapGet("/api/families/{familyId:int}/windows", (HttpContext ctx, int familyId, long? runId, IInvestigationStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetWindows(familyId, runId));
});

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/graph", (int familyId, long windowStart, long? runId, double? minWeight, int? maxNodes, int? maxEdges, string? predicate, string? nodeKind, IInvestigationStore store) =>
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
});

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/nodes/{nodeId:guid}", (int familyId, long windowStart, Guid nodeId, long? runId, IInvestigationStore store) =>
    ToResult(store.GetNodeDetail(familyId, windowStart, nodeId, runId)));

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/interactions/{source:guid}/{target:guid}", (int familyId, long windowStart, Guid source, Guid target, long? runId, IInvestigationStore store) =>
    ToResult(store.GetInteractionDetail(familyId, windowStart, source, target, runId)));

static IResult? ValidateGraphQuery(GraphQueryParameters parameters, IInvestigationStore store, long? runId)
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

app.Run();
