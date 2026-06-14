using CdmUuid = com.bbn.tc.schema.avro.cdm18.UUID;

namespace Spectre.CdmIngestion.Readers;

/// <summary>
/// Converts fixed UUID values into canonical .NET GUID values.
/// </summary>
public static class UuidConverter
{
    /// <summary>
    /// Converts a 16-byte UUID into a canonical <see cref="Guid"/>.
    /// </summary>
    /// <param name="uuid">UUID to convert.</param>
    /// <returns>The equivalent GUID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="uuid"/> is null.</exception>
    /// <exception cref="FormatException">Thrown when the fixed value does not contain exactly 16 bytes.</exception>
    public static Guid Convert(CdmUuid uuid)
    {
        ArgumentNullException.ThrowIfNull(uuid);

        if (uuid.Value is { Length: 16 } bytes)
        {
            return new Guid(bytes, bigEndian: true);
        }

        throw new FormatException("A CDM UUID must contain exactly 16 bytes.");
    }
}
