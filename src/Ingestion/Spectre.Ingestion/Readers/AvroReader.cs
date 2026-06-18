using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Avro.File;
using Avro.Specific;
using com.bbn.tc.schema.avro.cdm18;
using Spectre.Ingestion.Readers.Exceptions;
using CdmEvent = com.bbn.tc.schema.avro.cdm18.Event;

namespace Spectre.Ingestion.Readers;

/// <summary>
/// Streams and normalizes DARPA CDM18 Avro object-container segments using generated specific records.
/// </summary>
/// <param name="diagnosticLog">Optional callback used to report opened segment paths and writer schemas.</param>
public sealed class AvroReader(Action<string>? diagnosticLog = null) : ICdmRecordReader
{
    /// <summary>
    /// Expected full name of the embedded Avro writer schema.
    /// </summary>
    private const string ExpectedWriterSchemaFullName = "com.bbn.tc.schema.avro.cdm18.TCCDMDatum";

    /// <inheritdoc />
    /// <exception cref="SegmentReadException">
    /// Thrown during enumeration when the file cannot be opened, validated, or deserialized.
    /// </exception>
    public IEnumerable<SourcedCdmDatum> ReadFile(string path, CancellationToken cancellationToken)
    {
        return ReadFileIterator(Path.GetFullPath(path), cancellationToken);
    }

    private IEnumerable<SourcedCdmDatum> ReadFileIterator(
        string segmentPath,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                segmentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception exception)
        {
            throw Wrap(segmentPath, "Could not open Avro object-container segment.", exception);
        }

        IFileReader<TCCDMDatum> reader;

        try
        {
            reader = DataFileReader<TCCDMDatum>.OpenReader(
                stream,
                TCCDMDatum._SCHEMA,
                leaveOpen: true);
        }
        catch (Exception exception)
        {
            stream.Dispose();
            throw Wrap(segmentPath, "Could not open Avro object-container segment.", exception);
        }

        using (stream)
        using (reader)
        {
            string writerSchemaFullName;
            try
            {
                writerSchemaFullName = reader.GetSchema().Fullname;
            }
            catch (Exception exception)
            {
                throw Wrap(segmentPath, "Could not read the embedded Avro writer schema.", exception);
            }

            if (!string.Equals(
                    writerSchemaFullName,
                    ExpectedWriterSchemaFullName,
                    StringComparison.Ordinal))
            {
                throw Wrap(
                    segmentPath,
                    $"Unexpected Avro writer schema '{writerSchemaFullName}'. Expected '{ExpectedWriterSchemaFullName}'.");
            }

            diagnosticLog?.Invoke($"Reading {segmentPath} ({writerSchemaFullName})");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hasNext;
                try
                {
                    hasNext = reader.HasNext();
                }
                catch (Exception exception)
                {
                    throw Wrap(segmentPath, "Could not read the next Avro block.", exception);
                }

                if (!hasNext)
                {
                    yield break;
                }

                long syncBlockOffset;
                TCCDMDatum datum;
                try
                {
                    syncBlockOffset = reader.PreviousSync();
                    datum = reader.Next();
                }
                catch (Exception exception)
                {
                    throw Wrap(segmentPath, "Could not deserialize the next CDM18 datum.", exception);
                }

                yield return Normalize(
                    datum,
                    new SourceLocation(segmentPath, syncBlockOffset));
            }
        }
    }

    private static SourcedCdmDatum Normalize(TCCDMDatum envelope, SourceLocation source)
    {
        if (envelope.datum is null)
        {
            return new SourcedMalformedDatum("TCCDMDatum", "The top-level datum is null.", source);
        }

        return envelope.datum switch
        {
            CdmEvent value => NormalizeEvent(value, source),
            Subject value => NormalizeSubject(value, source),
            FileObject value => NormalizeEntity(value, value.uuid, "FileObject", "FILE_OBJECT_FILE", source),
            NetFlowObject value => NormalizeEntity(value, value.uuid, "NetFlowObject", "NETFLOW_OBJECT", source),
            UnnamedPipeObject value => NormalizeEntity(value, value.uuid, "UnnamedPipeObject", "UNNAMED_PIPE_OBJECT", source),
            MemoryObject value => NormalizeEntity(value, value.uuid, "MemoryObject", "MEMORY_OBJECT", source),
            RegistryKeyObject value => NormalizeEntity(value, value.uuid, "RegistryKeyObject", "REGISTRY_KEY_OBJECT", source),
            PacketSocketObject value => NormalizeEntity(value, value.uuid, "PacketSocketObject", "PACKET_SOCKET_OBJECT", source),
            SrcSinkObject value => NormalizeEntity(value, value.uuid, "SrcSinkObject", "SRCSINK_OBJECT", source),
            _ => new SourcedUnsupportedDatum(envelope.datum.GetType().Name, source)
        };
    }

    private static SourcedCdmDatum NormalizeEvent(CdmEvent value, SourceLocation source)
    {
        try
        {
            Guid? eventId = TryConvertOptionalUuid(value.uuid);
            Guid? subjectId = ConvertOptionalRequiredUuid(value.subject);
            Guid? objectId = ConvertOptionalRequiredUuid(value.predicateObject);
            Guid? object2Id = ConvertOptionalRequiredUuid(value.predicateObject2);

            return new SourcedEventDatum(
                eventId,
                subjectId,
                objectId,
                object2Id,
                value.type.ToString(),
                value.timestampNanos,
                source);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return new SourcedMalformedDatum("Event", exception.Message, source);
        }
    }

    private static SourcedCdmDatum NormalizeSubject(Subject value, SourceLocation source)
    {
        var rawSubtype = value.type.ToString();
        var unknownSubtype = false;
        var nodeKind = value.type switch
        {
            SubjectType.SUBJECT_PROCESS => "SUBJECT_PROCESS",
            SubjectType.SUBJECT_THREAD => "SUBJECT_THREAD",
            SubjectType.SUBJECT_UNIT => "SUBJECT_UNIT",
            SubjectType.SUBJECT_BASIC_BLOCK => "SUBJECT_BASIC_BLOCK",
            _ => MarkUnknownSubtype()
        };

        string MarkUnknownSubtype()
        {
            unknownSubtype = true;
            return "SUBJECT_PROCESS";
        }

        var normalized = NormalizeEntity(
            value,
            value.uuid,
            "Subject",
            nodeKind,
            source,
            unknownSubtype,
            "type");

        if (normalized is SourcedEntityDatum entity && !string.IsNullOrWhiteSpace(rawSubtype))
        {
            var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in entity.Attributes)
            {
                attributes.Add(attribute.Key, attribute.Value);
            }

            attributes["HAS_SUBJECT_TYPE"] = rawSubtype;

            return entity with { Attributes = attributes };
        }

        return normalized;
    }

    private static SourcedCdmDatum NormalizeEntity(
        ISpecificRecord value,
        UUID? uuid,
        string cdmType,
        string nodeKind,
        SourceLocation source,
        bool unknownSubjectSubtype = false,
        params string[] excludedProperties)
    {
        Guid entityId;
        try
        {
            if (uuid is null)
            {
                throw new FormatException($"{cdmType} is missing its required UUID.");
            }

            entityId = UuidConverter.Convert(uuid);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return new SourcedMalformedDatum(cdmType, exception.Message, source);
        }

        var excluded = new HashSet<string>(excludedProperties, StringComparer.OrdinalIgnoreCase)
        {
            "Schema",
            "uuid",
            "baseObject",
            "properties"
        };

        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        FlattenRecordScalars(value, attributes, excluded, prefix: null);
        FlattenStringProperties(value, attributes, "HAS_PROPERTY_");

        var baseObject = value.GetType().GetProperty(
            "baseObject",
            BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as AbstractObject;

        if (baseObject is not null)
        {
            FlattenRecordScalars(
                baseObject,
                attributes,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Schema",
                    "permission",
                    "properties"
                },
                "BASE_OBJECT_");
            FlattenStringProperties(baseObject, attributes, "HAS_BASE_PROPERTY_");
        }

        return new SourcedEntityDatum(
            entityId,
            cdmType,
            nodeKind,
            attributes,
            unknownSubjectSubtype,
            source);
    }

    private static void FlattenRecordScalars(
        object record,
        IDictionary<string, string> attributes,
        ISet<string> excluded,
        string? prefix)
    {
        foreach (var property in record.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (excluded.Contains(property.Name) || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(record);
            }
            catch
            {
                continue;
            }

            if (!TryFormatScalar(value, out var literal))
            {
                continue;
            }

            var predicate = $"HAS_{prefix}{ToUpperSnakeCase(property.Name)}";
            attributes.TryAdd(predicate, literal);
        }
    }

    private static void FlattenStringProperties(
        object record,
        IDictionary<string, string> attributes,
        string predicatePrefix)
    {
        object? rawProperties;
        try
        {
            rawProperties = record.GetType().GetProperty(
                "properties",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(record);
        }
        catch
        {
            return;
        }

        if (rawProperties is not IDictionary<string, string?> properties)
        {
            return;
        }

        foreach (var pair in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null)
            {
                continue;
            }

            var normalizedKey = ToUpperSnakeCase(pair.Key);
            if (normalizedKey.Length != 0)
            {
                attributes.TryAdd(predicatePrefix + normalizedKey, pair.Value);
            }
        }
    }

    private static bool TryFormatScalar(object? value, out string literal)
    {
        switch (value)
        {
            case null:
                literal = string.Empty;
                return false;
            case string text:
                literal = text;
                return true;
            case UUID uuid:
                try
                {
                    literal = UuidConverter.Convert(uuid).ToString("D");
                    return true;
                }
                catch
                {
                    literal = string.Empty;
                    return false;
                }
            case Enum enumValue:
                literal = enumValue.ToString();
                return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                literal = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            case byte[] or SpecificFixed or IEnumerable or IDictionary:
                literal = string.Empty;
                return false;
            default:
                literal = string.Empty;
                return false;
        }
    }

    private static Guid? ConvertOptionalRequiredUuid(UUID? uuid)
    {
        return uuid is null ? null : UuidConverter.Convert(uuid);
    }

    private static Guid? TryConvertOptionalUuid(UUID? uuid)
    {
        if (uuid is null)
        {
            return null;
        }

        try
        {
            return UuidConverter.Convert(uuid);
        }
        catch
        {
            return null;
        }
    }

    private static string ToUpperSnakeCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        var previousWasSeparator = true;

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                if (!previousWasSeparator && result.Length != 0)
                {
                    result.Append('_');
                }

                previousWasSeparator = true;
                continue;
            }

            if (char.IsUpper(character) && !previousWasSeparator && result.Length != 0)
            {
                result.Append('_');
            }

            result.Append(char.ToUpperInvariant(character));
            previousWasSeparator = false;
        }

        return result.ToString().Trim('_');
    }

    private static SegmentReadException Wrap(string path, string message, Exception? inner = null)
    {
        return new SegmentReadException(path, message, inner);
    }
}
