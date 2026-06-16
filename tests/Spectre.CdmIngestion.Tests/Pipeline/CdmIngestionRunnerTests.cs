using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Pipeline;
using Spectre.CdmIngestion.Projection;
using Spectre.CdmIngestion.Sinks;

namespace Spectre.CdmIngestion.Tests.Pipeline;

public sealed class CdmIngestionRunnerTests
{
    [Fact]
    public void ValidationFailure_DoesNotCreateSinkOrStartIngestion()
    {
        using var temp = new TempDirectory();
        System.IO.File.WriteAllText(temp.File("family.bin.1"), "");
        var sinkCreated = false;
        var runner = CreateRunner([]);

        var result = runner.Run(
            [temp.Path],
            () =>
            {
                sinkCreated = true;
                return new NullGraphFactSink();
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestionOutcome.Failed, result.Outcome);
        Assert.False(sinkCreated);
        Assert.Null(result.Metrics.ProcessingStartedAt);
        Assert.NotEqual(default, result.Metrics.ProcessingEndedAt);
    }

    [Fact]
    public void Cancellation_PreservesPartialMetricsWithoutCountingCurrentFileOrFamily()
    {
        using var temp = new TempDirectory();
        System.IO.File.WriteAllText(temp.File("family.bin"), "");
        using var cancellation = new CancellationTokenSource();
        var datum = new SourcedEntityDatum(
            Guid.NewGuid(),
            "Subject",
            "SUBJECT_PROCESS",
            new Dictionary<string, string> { ["HAS_ALPHA"] = "a" },
            false,
            new SourceLocation(temp.File("family.bin"), 0));
        var runner = CreateRunner([datum]);

        var result = runner.Run(
            [temp.Path],
            () => new CancelAfterWriteSink(cancellation),
            cancellation.Token);

        Assert.Equal(IngestionOutcome.Canceled, result.Outcome);
        Assert.Equal(1, result.Metrics.RecordsRead);
        Assert.Equal(1, result.Metrics.FactsWritten);
        Assert.Equal(0, result.Metrics.InputFilesProcessed);
        Assert.Equal(0, result.Metrics.InputFamiliesProcessed);
        Assert.NotNull(result.Metrics.ProcessingStartedAt);
        Assert.NotEqual(default, result.Metrics.ProcessingEndedAt);
    }

    [Fact]
    public void CompletedRun_NotifiesFamilyAwareSinkAroundEachFamily()
    {
        using var temp = new TempDirectory();
        var first = temp.File("a.bin");
        var second = temp.File("b.bin");
        System.IO.File.WriteAllText(first, "");
        System.IO.File.WriteAllText(second, "");
        var sink = new FamilyTrackingSink();
        var runner = CreateRunner([]);

        var result = runner.Run([temp.Path], () => sink, TestContext.Current.CancellationToken);

        Assert.Equal(IngestionOutcome.Completed, result.Outcome);
        Assert.Equal(
            [
                $"BEGIN:{Path.GetFullPath(first)}",
                $"END:{Path.GetFullPath(first)}",
                $"BEGIN:{Path.GetFullPath(second)}",
                $"END:{Path.GetFullPath(second)}"
            ],
            sink.Events);
    }

    private static CdmIngestionRunner CreateRunner(IEnumerable<SourcedCdmDatum> data)
    {
        return new CdmIngestionRunner(
            new IngestionPipeline(
                new StubReader(data),
                new GraphFactProjector()));
    }

    private sealed class CancelAfterWriteSink(CancellationTokenSource cancellation) : IGraphFactSink
    {
        public void Write(GraphFact fact) => cancellation.Cancel();

        public void Dispose()
        {
        }
    }

    private sealed class FamilyTrackingSink : IGraphFactFamilySink
    {
        public List<string> Events { get; } = [];

        public void BeginFamily(string familyBasePath) => Events.Add($"BEGIN:{familyBasePath}");

        public void EndFamily(string familyBasePath) => Events.Add($"END:{familyBasePath}");

        public void Write(GraphFact fact)
        {
        }

        public void Dispose()
        {
        }
    }
}
