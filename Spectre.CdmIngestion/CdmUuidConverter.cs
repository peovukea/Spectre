using CdmUuid = com.bbn.tc.schema.avro.cdm18.UUID;

namespace Spectre.CdmIngestion;

/// <summary>
/// Converts CDM fixed UUID values into canonical .NET GUID values.
/// </summary>
public static class CdmUuidConverter
{
    /// <summary>
    /// Converts a 16-byte, network-order CDM UUID into a canonical <see cref="Guid"/>.
    /// </summary>
    /// <param name="uuid">CDM UUID to convert.</param>
    /// <returns>The equivalent GUID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="uuid"/> is null.</exception>
    /// <exception cref="FormatException">Thrown when the fixed value does not contain exactly 16 bytes.</exception>
    public static Guid Convert(CdmUuid uuid)
    {
        ArgumentNullException.ThrowIfNull(uuid);

        if (uuid.Value is not { Length: 16 } bytes)
        {
            throw new FormatException("A CDM UUID must contain exactly 16 bytes.");
        }

        return new Guid(bytes, bigEndian: true);
    }
}
