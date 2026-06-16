using Spectre.CdmIngestion;
using Spectre.CdmIngestion.Pipeline;

namespace Spectre.CdmIngestion.Tests.Pipeline;

public sealed class CdmInputFamilyDiscoveryTests
{
    [Fact]
    public void DirectoryInput_GroupsAndOrdersFamiliesAndSegmentsGlobally()
    {
        using var temp = new TempDirectory();
        System.IO.File.WriteAllText(temp.File("z.bin.2"), "");
        System.IO.File.WriteAllText(temp.File("z.bin"), "");
        System.IO.File.WriteAllText(temp.File("z.bin.1"), "");
        System.IO.File.WriteAllText(temp.File("a.bin"), "");
        System.IO.File.WriteAllText(temp.File("ignore.binlog"), "");

        var families = CdmInputFamilyDiscovery.Resolve([temp.Path]);

        Assert.Equal(["a.bin", "z.bin"], families.Select(family => Path.GetFileName(family.BasePath)));
        Assert.Equal(
            ["z.bin", "z.bin.1", "z.bin.2"],
            families[1].SegmentPaths.Select(Path.GetFileName));
    }

    [Fact]
    public void DirectSegmentInput_IsRejected()
    {
        using var temp = new TempDirectory();
        System.IO.File.WriteAllText(temp.File("family.bin"), "");
        System.IO.File.WriteAllText(temp.File("family.bin.1"), "");

        Assert.Throws<InputValidationException>(
            () => CdmInputFamilyDiscovery.Resolve([temp.File("family.bin.1")]));
    }

    [Fact]
    public void DuplicatePhysicalFamilyAcrossInputs_IsRejected()
    {
        using var temp = new TempDirectory();
        var basePath = temp.File("family.bin");
        System.IO.File.WriteAllText(basePath, "");

        Assert.Throws<InputValidationException>(
            () => CdmInputFamilyDiscovery.Resolve([temp.Path, basePath]));
    }

    [Fact]
    public void MissingSegment_IsRejected()
    {
        using var temp = new TempDirectory();
        System.IO.File.WriteAllText(temp.File("family.bin"), "");
        System.IO.File.WriteAllText(temp.File("family.bin.2"), "");

        Assert.Throws<InputValidationException>(
            () => CdmInputFamilyDiscovery.Resolve([temp.Path]));
    }
}
