using com.bbn.tc.schema.avro.cdm18;
using Spectre.CdmIngestion;

namespace Spectre.CdmIngestion.Tests;

public sealed class CdmUuidConverterTests
{
    [Fact]
    public void Convert_UsesBigEndianUuidLayout()
    {
        var uuid = new UUID
        {
            Value = Convert.FromHexString("00112233445566778899aabbccddeeff")
        };

        var result = CdmUuidConverter.Convert(uuid);

        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", result.ToString("D"));
    }
}
