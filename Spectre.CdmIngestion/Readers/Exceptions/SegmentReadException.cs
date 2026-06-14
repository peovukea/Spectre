namespace Spectre.CdmIngestion.Readers.Exceptions;

/// <summary>
/// Reports a physical segment open, schema, or deserialization failure.
/// </summary>
public sealed class SegmentReadException : IOException
{
    /// <summary>
    /// Initializes a segment read exception.
    /// </summary>
    /// <param name="segmentPath">Physical segment associated with the failure.</param>
    /// <param name="message">Failure description.</param>
    /// <param name="innerException">Underlying I/O or Avro exception, when available.</param>
    public SegmentReadException(string segmentPath, string message, System.Exception? innerException = null)
        : base($"{message} Segment: '{segmentPath}'.", innerException)
    {
        SegmentPath = segmentPath;
    }

    /// <summary>
    /// Gets the physical segment associated with the failure.
    /// </summary>
    public string SegmentPath { get; }
}
