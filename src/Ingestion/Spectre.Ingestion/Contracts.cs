namespace Spectre.Ingestion;

/// <summary>
/// Identifies the physical Avro segment and sync block from which a CDM datum was read.
/// </summary>
/// <param name="SegmentPath">Absolute path of the physical object-container segment.</param>
/// <param name="SyncBlockOffset">
/// Avro sync-block offset captured from <c>PreviousSync()</c>; this is not an exact record offset.
/// </param>
public sealed record SourceLocation(string SegmentPath, long SyncBlockOffset);

/// <summary>
/// Base type for a graph fact emitted by CDM graph-fact ingestion.
/// </summary>
/// <param name="SubjectId">Identifier of the graph node described by the fact.</param>
/// <param name="Predicate">Relationship or attribute predicate.</param>
/// <param name="TimestampNanos">Event timestamp in Unix-epoch nanoseconds, when available.</param>
/// <param name="Source">Physical source location of the originating CDM datum.</param>
public abstract record GraphFact(
    Guid SubjectId,
    string Predicate,
    long? TimestampNanos,
    SourceLocation Source);

/// <summary>
/// Describes a directed relationship between two graph nodes.
/// </summary>
/// <param name="SubjectId">Identifier of the source node.</param>
/// <param name="Predicate">Event type used as the edge predicate.</param>
/// <param name="ObjectId">Identifier of the target node.</param>
/// <param name="TimestampNanos">Event timestamp in Unix-epoch nanoseconds.</param>
/// <param name="Source">Physical source location of the originating event.</param>
/// <param name="EventId">Identifier of the originating event, when available.</param>
public sealed record EdgeFact(
    Guid SubjectId,
    string Predicate,
    Guid ObjectId,
    long? TimestampNanos,
    SourceLocation Source,
    Guid? EventId = null)
    : GraphFact(SubjectId, Predicate, TimestampNanos, Source);

/// <summary>
/// Describes a literal attribute attached to a graph node.
/// </summary>
/// <param name="SubjectId">Identifier of the described node.</param>
/// <param name="Predicate">Attribute predicate.</param>
/// <param name="LiteralValue">Normalized string representation of the attribute value.</param>
/// <param name="TimestampNanos">Timestamp when available; entity metadata normally has no timestamp.</param>
/// <param name="Source">Physical source location of the originating entity record.</param>
public sealed record AttributeFact(
    Guid SubjectId,
    string Predicate,
    string LiteralValue,
    long? TimestampNanos,
    SourceLocation Source)
    : GraphFact(SubjectId, Predicate, TimestampNanos, Source);

/// <summary>
/// Base type for a normalized CDM datum paired with its physical source location.
/// </summary>
/// <param name="Source">Physical source location of the datum.</param>
public abstract record SourcedCdmDatum(SourceLocation Source);

/// <summary>
/// Normalized CDM event datum that can project to zero, one, or two edge facts.
/// </summary>
public sealed record SourcedEventDatum(
    Guid? EventId,
    Guid? SubjectId,
    Guid? PredicateObjectId,
    Guid? PredicateObject2Id,
    string EventType,
    long TimestampNanos,
    SourceLocation Source)
    : SourcedCdmDatum(Source);

/// <summary>
/// Normalized supported CDM entity datum that projects to attribute facts.
/// </summary>
public sealed record SourcedEntityDatum(
    Guid EntityId,
    string CdmType,
    string NodeKind,
    IReadOnlyDictionary<string, string> Attributes,
    bool UnknownSubjectSubtype,
    SourceLocation Source)
    : SourcedCdmDatum(Source);

/// <summary>
/// Represents a valid CDM datum whose record type is outside the supported ingestion scope.
/// </summary>
public sealed record SourcedUnsupportedDatum(string CdmType, SourceLocation Source)
    : SourcedCdmDatum(Source);

/// <summary>
/// Represents a supported CDM datum that could not satisfy required normalization rules.
/// </summary>
public sealed record SourcedMalformedDatum(string CdmType, string Reason, SourceLocation Source)
    : SourcedCdmDatum(Source);

/// <summary>
/// Streams normalized sourced CDM datums from a physical object-container segment.
/// </summary>
public interface ICdmRecordReader
{
    /// <summary>
    /// Lazily reads normalized datums from one physical Avro segment.
    /// </summary>
    /// <param name="path">Path to the physical Avro object-container segment.</param>
    /// <param name="cancellationToken">Token checked cooperatively between datums.</param>
    /// <returns>A synchronous lazy sequence that owns its file resources while enumerated.</returns>
    IEnumerable<SourcedCdmDatum> ReadFile(string path, CancellationToken cancellationToken);
}
