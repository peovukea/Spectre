using System.Text;
using System.Text.Json;

namespace Spectre.CdmIngestion.Sinks;

/// <summary>
/// Writes a bounded, buffered JSONL sample of the graph-fact stream.
/// </summary>
public sealed class SampleJsonlGraphFactSink : IGraphFactSink
{
    /// <summary>
    /// Default maximum number of graph facts written to a sample file.
    /// </summary>
    public const int DefaultSampleLimit = 50_000;

    private readonly StreamWriter _writer;
    private readonly int _sampleLimit;
    private int _factsWritten;
    private bool _disposed;

    /// <summary>
    /// Initializes a bounded JSONL sample sink and creates or replaces its output file.
    /// </summary>
    /// <param name="path">JSONL output path.</param>
    /// <param name="sampleLimit">Maximum number of facts to write before becoming a successful no-op.</param>
    public SampleJsonlGraphFactSink(string path, int sampleLimit = DefaultSampleLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleLimit);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _sampleLimit = sampleLimit;
        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024)
        {
            NewLine = "\n"
        };
    }

    /// <inheritdoc />
    public void Write(GraphFact fact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fact);

        if (_factsWritten >= _sampleLimit)
        {
            return;
        }

        var json = fact switch
        {
            EdgeFact edge => JsonSerializer.Serialize(new
            {
                kind = "Edge",
                subjectId = edge.SubjectId,
                predicate = edge.Predicate,
                objectId = edge.ObjectId,
                timestampNanos = edge.TimestampNanos,
                eventId = edge.EventId,
                sourceFile = edge.Source.SegmentPath,
                sourceOffset = edge.Source.SyncBlockOffset
            }),
            AttributeFact attribute => JsonSerializer.Serialize(new
            {
                kind = "Attribute",
                subjectId = attribute.SubjectId,
                predicate = attribute.Predicate,
                literalValue = attribute.LiteralValue,
                timestampNanos = attribute.TimestampNanos,
                sourceFile = attribute.Source.SegmentPath,
                sourceOffset = attribute.Source.SyncBlockOffset
            }),
            _ => throw new ArgumentException($"Unsupported graph fact type '{fact.GetType().Name}'.", nameof(fact))
        };

        _writer.WriteLine(json);
        _factsWritten++;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
    }
}
