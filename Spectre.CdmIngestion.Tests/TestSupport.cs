using Avro.File;
using Avro.Specific;
using com.bbn.tc.schema.avro.cdm18;
using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Sinks;

namespace Spectre.CdmIngestion.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Spectre.CdmIngestion.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class AvroFixtureBuilder
{
    public static void Write(string path, params TCCDMDatum[] data)
    {
        var datumWriter = new SpecificDatumWriter<TCCDMDatum>(TCCDMDatum._SCHEMA);
        using var writer = DataFileWriter<TCCDMDatum>.OpenWriter(datumWriter, path);

        foreach (var datum in data)
        {
            writer.Append(datum);
            writer.Sync();
        }
    }

    public static UUID Uuid(Guid value)
    {
        return new UUID { Value = value.ToByteArray(bigEndian: true) };
    }

    public static TCCDMDatum Envelope(object datum)
    {
        return new TCCDMDatum
        {
            datum = datum,
            CDMVersion = "18",
            source = InstrumentationSource.SOURCE_FREEBSD_DTRACE_CADETS
        };
    }

    public static Subject Subject(Guid id, SubjectType type = SubjectType.SUBJECT_PROCESS)
    {
        return new Subject
        {
            uuid = Uuid(id),
            type = type,
            cid = 42,
            hostId = Uuid(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            localPrincipal = Uuid(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            startTimestampNanos = 123
        };
    }
}

internal sealed class CollectingSink : IGraphFactSink
{
    public List<GraphFact> Facts { get; } = [];

    public void Write(GraphFact fact) => Facts.Add(fact);

    public void Dispose()
    {
    }
}

internal sealed class StubReader(IEnumerable<SourcedCdmDatum> data) : ICdmRecordReader
{
    public IEnumerable<SourcedCdmDatum> ReadFile(string path, CancellationToken cancellationToken)
    {
        foreach (var datum in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return datum;
        }
    }
}
