using com.bbn.tc.schema.avro.cdm18;
using Spectre.CdmIngestion.Readers;

namespace Spectre.CdmIngestion.Tests.Reading;

public sealed class UuidConverterTests
{
    [Fact]
    public void Convert_UsesBigEndianUuidLayout()
    {
        var uuid = new UUID
        {
            Value = Convert.FromHexString("00112233445566778899aabbccddeeff")
        };

        var result = UuidConverter.Convert(uuid);

        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", result.ToString("D"));
    }
}
