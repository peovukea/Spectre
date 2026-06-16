using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.InvestigationHost;
using Spectre.InvestigationHost.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<DashboardQueryStore>();
builder.Services.AddHostedService<IngestionBackgroundService>();
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new LongAsStringJsonConverter());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseCors();

// Ensure metadata endpoints don't cache during active run
void ApplyNoStore(HttpContext ctx, DashboardQueryStore store)
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

    var stream = hub.Subscribe(ct);
    await using var enumerator = stream.GetAsyncEnumerator(ct);
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
});

app.MapGet("/api/status", (DashboardQueryStore store) =>
{
    return Results.Ok(store.GetRunStatus());
});

app.MapGet("/api/memory", (DashboardQueryStore store) =>
{
    return Results.Ok(store.GetMemoryPressure());
});

app.MapGet("/api/families", (HttpContext ctx, DashboardQueryStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetFamilies());
});

app.MapGet("/api/predicates", (HttpContext ctx, DashboardQueryStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetPredicates());
});

app.MapGet("/api/node-kinds", (HttpContext ctx, DashboardQueryStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetNodeKinds());
});

app.MapGet("/api/families/{familyId:int}/windows", (HttpContext ctx, int familyId, DashboardQueryStore store) =>
{
    ApplyNoStore(ctx, store);
    return Results.Ok(store.GetWindows(familyId));
});

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/graph", (int familyId, long windowStart, double? minWeight, int? maxNodes, int? maxEdges, string? predicate, string? nodeKind, DashboardQueryStore store) =>
{
    var p = new GraphQueryParameters(
        minWeight ?? 0.0,
        maxNodes ?? store.DefaultMaxNodes,
        maxEdges ?? store.DefaultMaxEdges,
        predicate,
        nodeKind);

    var validation = ValidateGraphQuery(p, store);
    if (validation is not null)
    {
        return validation;
    }

    return ToResult(store.GetProjection(familyId, windowStart, p));
});

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/nodes/{nodeId:guid}", (int familyId, long windowStart, Guid nodeId, DashboardQueryStore store) =>
    ToResult(store.GetNodeDetail(familyId, windowStart, nodeId)));

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/interactions/{source:guid}/{target:guid}", (int familyId, long windowStart, Guid source, Guid target, DashboardQueryStore store) =>
    ToResult(store.GetInteractionDetail(familyId, windowStart, source, target)));

static IResult? ValidateGraphQuery(GraphQueryParameters parameters, DashboardQueryStore store)
{
    var validation = GraphQueryValidator.Validate(parameters, store.GetPredicates(), store.GetNodeKinds());
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
