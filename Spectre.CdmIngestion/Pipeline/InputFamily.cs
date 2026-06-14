namespace Spectre.CdmIngestion.Pipeline;

/// <summary>
/// Describes one logical CDM family and its ordered physical object-container segments.
/// </summary>
/// <param name="BasePath">Absolute path of the family's base <c>.bin</c> segment.</param>
/// <param name="SegmentPaths">Absolute segment paths ordered from base through numeric suffixes.</param>
public sealed record InputFamily(string BasePath, IReadOnlyList<string> SegmentPaths);
