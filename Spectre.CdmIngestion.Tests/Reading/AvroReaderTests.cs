using Avro.File;
using Avro.Specific;
using com.bbn.tc.schema.avro.cdm18;
using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Reading;

namespace Spectre.CdmIngestion.Tests.Reading;

public sealed class AvroReaderTests
{
    [Fact]
    public void ReadFile_IsLazyAndDoesNotOpenUntilEnumeration()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.bin");
        var data = new AvroReader().ReadFile(path, CancellationToken.None);

        var exception = Assert.Throws<SegmentReadException>(() => data.ToArray());

        Assert.Contains(Path.GetFullPath(path), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_UsesSpecificRecordsAndCapturesSyncBlockOffsets()
    {
        using var temp = new TempDirectory();
        var path = temp.File("family.bin");
        var firstId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondId = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff");
        AvroFixtureBuilder.Write(
            path,
            AvroFixtureBuilder.Envelope(AvroFixtureBuilder.Subject(firstId, SubjectType.SUBJECT_THREAD)),
            AvroFixtureBuilder.Envelope(AvroFixtureBuilder.Subject(secondId)));

        var data = new AvroReader()
            .ReadFile(path, CancellationToken.None)
            .Cast<SourcedEntityDatum>()
            .ToArray();

        Assert.Equal([firstId, secondId], data.Select(datum => datum.EntityId));
        Assert.Equal("SUBJECT_THREAD", data[0].NodeKind);
        Assert.Equal("SUBJECT_THREAD", data[0].Attributes["HAS_SUBJECT_TYPE"]);
        Assert.True(data[1].Source.SyncBlockOffset > data[0].Source.SyncBlockOffset);
    }

    [Fact]
    public void IncompatibleWriterSchema_FailsWithSegmentPath()
    {
        using var temp = new TempDirectory();
        var path = temp.File("family.bin");
        var datumWriter = new SpecificDatumWriter<StartMarker>(StartMarker._SCHEMA);
        using (var writer = DataFileWriter<StartMarker>.OpenWriter(datumWriter, path))
        {
            writer.Append(new StartMarker { sessionNumber = 1 });
        }

        var exception = Assert.Throws<SegmentReadException>(
            () => new AvroReader().ReadFile(path, CancellationToken.None).ToArray());

        Assert.Contains(Path.GetFullPath(path), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedContainer_FailsWithSegmentPath()
    {
        using var temp = new TempDirectory();
        var validPath = temp.File("valid.bin");
        var truncatedPath = temp.File("family.bin");
        AvroFixtureBuilder.Write(
            validPath,
            AvroFixtureBuilder.Envelope(AvroFixtureBuilder.Subject(Guid.NewGuid())));

        var bytes = System.IO.File.ReadAllBytes(validPath);
        System.IO.File.WriteAllBytes(truncatedPath, bytes[..^8]);

        var exception = Assert.Throws<SegmentReadException>(
            () => new AvroReader()
                .ReadFile(truncatedPath, CancellationToken.None)
                .ToArray());

        Assert.Contains(Path.GetFullPath(truncatedPath), exception.Message, StringComparison.Ordinal);
    }
}
