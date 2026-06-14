using System.Text.Json;
using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Pipeline;
using Spectre.CdmIngestion.Projection;
using Spectre.CdmIngestion.Readers;
using Spectre.CdmIngestion.Sinks;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    CliOptions.PrintUsage();
    return 2;
}

if (options.ShowHelp)
{
    CliOptions.PrintUsage();
    return 0;
}

using var cancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var reader = new AvroReader(Console.Error.WriteLine);
    var projector = new GraphFactProjector();
    var pipeline = new IngestionPipeline(reader, projector);
    var runner = new CdmIngestionRunner(pipeline);

    var result = runner.Run(
        options.Inputs,
        () => CreateSink(options),
        cancellationSource.Token);

    Console.WriteLine(JsonSerializer.Serialize(
        result.Metrics,
        new JsonSerializerOptions { WriteIndented = true }));

    if (result.Exception is not null)
    {
        Console.Error.WriteLine(result.Exception);
    }

    return result.Outcome switch
    {
        IngestionOutcome.Completed => 0,
        IngestionOutcome.Canceled => 130,
        IngestionOutcome.Failed => 1,
        _ => 1
    };
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static IGraphFactSink CreateSink(CliOptions options)
{
    if (options.MetricsOnly)
    {
        return new NullGraphFactSink();
    }

    return new CompositeGraphFactSink(
    [
        new SampleJsonlGraphFactSink(options.SampleOutput, options.SampleLimit)
    ]);
}

internal sealed record CliOptions(
    IReadOnlyList<string> Inputs,
    string SampleOutput,
    int SampleLimit,
    bool MetricsOnly,
    bool ShowHelp)
{
    private const string DefaultSampleOutput = "output/graphfacts.sample.jsonl";

    public static CliOptions Parse(string[] arguments)
    {
        var inputs = new List<string>();
        var sampleOutput = DefaultSampleOutput;
        var sampleLimit = SampleJsonlGraphFactSink.DefaultSampleLimit;
        var metricsOnly = false;
        var showHelp = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--input":
                    inputs.Add(ReadValue(arguments, ref index, "--input"));
                    break;
                case "--sample-output":
                    sampleOutput = ReadValue(arguments, ref index, "--sample-output");
                    break;
                case "--sample-limit":
                    var rawLimit = ReadValue(arguments, ref index, "--sample-limit");
                    if (!int.TryParse(rawLimit, out sampleLimit) || sampleLimit < 0)
                    {
                        throw new ArgumentException("--sample-limit must be a non-negative integer.");
                    }

                    break;
                case "--metrics-only":
                    metricsOnly = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: '{arguments[index]}'.");
            }
        }

        if (!showHelp && inputs.Count == 0)
        {
            throw new ArgumentException("At least one --input path is required.");
        }

        return new CliOptions(inputs, sampleOutput, sampleLimit, metricsOnly, showHelp);
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            CDM Graph-Fact Ingestion

            Usage:
              Spectre.CdmIngestion.Cli --input <directory-or-family.bin> [--input <path> ...]
                  [--sample-output <path>] [--sample-limit <count>] [--metrics-only]

            Defaults:
              --sample-output output/graphfacts.sample.jsonl
              --sample-limit  50000
            """);
    }

    private static string ReadValue(string[] arguments, ref int index, string option)
    {
        if (++index >= arguments.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index];
    }
}
