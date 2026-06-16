using Spectre.Investigation.Api;
using Spectre.InvestigationHost.Store;
using System.Text.Json;

namespace Spectre.InvestigationHost.Tests;

public sealed class RuntimeSurfaceTests
{
    [Theory]
    [InlineData(-0.1, 250, 200, null, null, "minWeight must be a finite number greater than or equal to 0.")]
    [InlineData(0.0, 1, 200, null, null, "maxNodes must be between 2 and 1000.")]
    [InlineData(0.0, 250, 0, null, null, "maxEdges must be between 1 and 2000.")]
    [InlineData(0.0, 250, 200, "EXEC", null, "Unknown predicate 'EXEC'.")]
    [InlineData(0.0, 250, 200, null, "FILE", "Unknown node kind 'FILE'.")]
    public void GraphQueryValidator_RejectsInvalidParameters(
        double minWeight,
        int maxNodes,
        int maxEdges,
        string? predicate,
        string? nodeKind,
        string expectedError)
    {
        var result = GraphQueryValidator.Validate(
            new GraphQueryParameters(minWeight, maxNodes, maxEdges, predicate, nodeKind),
            new HashSet<string> { "READ", "WRITE" },
            new HashSet<string> { "PROCESS" });

        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void LongAsStringJsonConverter_WritesAndReadsInt64AsString()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new LongAsStringJsonConverter() }
        };
        var dto = new StoreMemoryPressureDto(1, 2, 3, 9_007_199_254_740_993, 4, 5, 6, 7, 8, 9, 10, 11);

        var json = JsonSerializer.Serialize(dto, options);
        var roundTripped = JsonSerializer.Deserialize<StoreMemoryPressureDto>(json, options);

        Assert.Contains("\"estimatedDetailedBytes\":\"9007199254740993\"", json);
        Assert.Equal(9_007_199_254_740_993, roundTripped!.EstimatedDetailedBytes);
    }

    [Fact]
    public async Task EventHub_AssignsMonotonicServerSentEventIds()
    {
        var hub = new EventHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = hub.Subscribe(cts.Token);
        var enumerator = events.GetAsyncEnumerator(cts.Token);

        try
        {
            hub.Publish(new ServerSentEvent("first", "{}"));
            hub.Publish(new ServerSentEvent("second", "{}"));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(1, enumerator.Current.Id);
            Assert.Equal("first", enumerator.Current.EventType);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(2, enumerator.Current.Id);
            Assert.Equal("second", enumerator.Current.EventType);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
