using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Spectre.InvestigationHost;
using Spectre.InvestigationHost.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<DashboardQueryStore>();
builder.Services.AddHostedService<IngestionBackgroundService>();
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
    
    var lastEventId = ctx.Request.Headers["Last-Event-ID"].ToString();
    // If there's a last event ID, we don't replay, the client must fetch /api/status 
    // to reconcile. But we still subscribe them to new events.

    int eventId = 0;
    
    // Heartbeat loop
    _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            try
            {
                await ctx.Response.WriteAsync($": heartbeat\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            catch { break; }
        }
    }, ct);

    await foreach (var sse in hub.Subscribe(ct))
    {
        eventId++;
        await ctx.Response.WriteAsync($"id: {eventId}\nevent: {sse.EventType}\ndata: {sse.Data}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
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

    try
    {
        var proj = store.GetProjection(familyId, windowStart, p);
        return proj != null ? Results.Ok(proj) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (ex.Message == "410 Gone")
    {
        return Results.StatusCode(410);
    }
});

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/nodes/{nodeId:guid}", (int familyId, long windowStart, Guid nodeId, DashboardQueryStore store) =>
{
    try
    {
        var node = store.GetNodeDetail(familyId, windowStart, nodeId);
        return node != null ? Results.Ok(node) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (ex.Message == "410 Gone")
    {
        return Results.StatusCode(410);
    }
});

app.MapGet("/api/families/{familyId:int}/windows/{windowStart:long}/interactions/{source:guid}/{target:guid}/{predicate}", (int familyId, long windowStart, Guid source, Guid target, string predicate, DashboardQueryStore store) =>
{
    try
    {
        var inter = store.GetInteractionDetail(familyId, windowStart, source, target, predicate);
        return inter != null ? Results.Ok(inter) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (ex.Message == "410 Gone")
    {
        return Results.StatusCode(410);
    }
});

app.Run();
