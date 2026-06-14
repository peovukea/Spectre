using System.Text;
using System.Text.Json;

namespace Spectre.CdmIngestion;

/// <summary>
/// Accepts and discards all graph facts.
/// </summary>
public sealed class NullGraphFactSink : IGraphFactSink
{
    /// <inheritdoc />
    public void Write(GraphFact fact)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

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
            bufferSize: 64 * 1024);
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

/// <summary>
/// Forwards each graph fact to multiple child sinks in configured order.
/// </summary>
public sealed class CompositeGraphFactSink : IGraphFactSink
{
    private readonly IReadOnlyList<IGraphFactSink> _sinks;
    private bool _disposed;

    /// <summary>
    /// Initializes a composite sink.
    /// </summary>
    /// <param name="sinks">Child sinks invoked in enumeration order.</param>
    public CompositeGraphFactSink(IEnumerable<IGraphFactSink> sinks)
    {
        _sinks = sinks?.ToArray() ?? throw new ArgumentNullException(nameof(sinks));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarding stops when a child throws. Child sinks that already accepted the fact are not rolled back.
    /// </remarks>
    public void Write(GraphFact fact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var sink in _sinks)
        {
            sink.Write(fact);
        }
    }

    /// <inheritdoc />
    /// <remarks>Child sinks are disposed in reverse order.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? exceptions = null;

        for (var index = _sinks.Count - 1; index >= 0; index--)
        {
            try
            {
                _sinks[index].Dispose();
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException("One or more graph-fact sinks failed during disposal.", exceptions);
        }
    }
}
