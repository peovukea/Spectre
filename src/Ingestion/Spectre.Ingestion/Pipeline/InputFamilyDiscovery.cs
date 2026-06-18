using System.Text.RegularExpressions;

namespace Spectre.Ingestion.Pipeline;

/// <summary>
/// Discovers, merges, orders, and validates CDM object-container families before ingestion begins.
/// </summary>
public static partial class InputFamilyDiscovery
{
    /// <summary>
    /// Resolves directory and base-family inputs into a globally ordered, validated family set.
    /// </summary>
    /// <param name="inputs">Directory paths or base <c>&lt;family&gt;.bin</c> paths.</param>
    /// <returns>Validated families ordered by absolute base path using ordinal comparison.</returns>
    /// <exception cref="InputValidationException">
    /// Thrown when an input is invalid, duplicated, missing its base, or has non-contiguous segments.
    /// </exception>
    public static IReadOnlyList<InputFamily> Resolve(IEnumerable<string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var inputList = inputs.ToArray();
        if (inputList.Length == 0)
        {
            throw new InputValidationException("At least one --input path is required.");
        }

        var families = new Dictionary<string, InputFamily>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputList)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new InputValidationException("Input paths cannot be empty.");
            }

            var fullPath = Path.GetFullPath(input);
            IReadOnlyList<InputFamily> discovered;

            if (Directory.Exists(fullPath))
            {
                discovered = DiscoverDirectory(fullPath);
            }
            else if (File.Exists(fullPath))
            {
                discovered = [DiscoverBaseFile(fullPath)];
            }
            else
            {
                throw new InputValidationException($"Input path does not exist: '{fullPath}'.");
            }

            foreach (var family in discovered)
            {
                if (!families.TryAdd(family.BasePath, family))
                {
                    throw new InputValidationException(
                        $"The same physical CDM family was supplied more than once: '{family.BasePath}'.");
                }
            }
        }

        if (families.Count == 0)
        {
            throw new InputValidationException(
                "No CDM object-container families matching '<family>.bin[.N]' were found.");
        }

        return families.Values
            .OrderBy(family => family.BasePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<InputFamily> DiscoverDirectory(string directoryPath)
    {
        var groups = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
        {
            var fileName = Path.GetFileName(filePath);
            var match = FamilyFileNameRegex().Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var basePath = Path.GetFullPath(Path.Combine(directoryPath, match.Groups["family"].Value));
            var segment = match.Groups["segment"].Success
                ? int.Parse(match.Groups["segment"].Value)
                : 0;

            var segments = groups.GetValueOrDefault(basePath);
            if (segments is null)
            {
                segments = new Dictionary<int, string>();
                groups.Add(basePath, segments);
            }

            if (!segments.TryAdd(segment, Path.GetFullPath(filePath)))
            {
                throw new InputValidationException(
                    $"Duplicate segment {segment} was found for family '{basePath}'.");
            }
        }

        return groups
            .Select(group => ValidateFamily(group.Key, group.Value))
            .OrderBy(family => family.BasePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static InputFamily DiscoverBaseFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var match = FamilyFileNameRegex().Match(fileName);

        if (!match.Success)
        {
            throw new InputValidationException(
                $"Input file must be a base '<family>.bin' path: '{filePath}'.");
        }

        if (match.Groups["segment"].Success)
        {
            throw new InputValidationException(
                $"Segment paths cannot be supplied directly. Pass the base family path instead of '{filePath}'.");
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InputValidationException($"Input file has no parent directory: '{filePath}'.");
        var basePath = Path.GetFullPath(filePath);
        var segments = new Dictionary<int, string>();
        var escapedBaseName = Regex.Escape(fileName);
        var segmentRegex = new Regex(
            $"^{escapedBaseName}(?:\\.(?<segment>[1-9]\\d*))?$",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var candidateMatch = segmentRegex.Match(Path.GetFileName(candidate));
            if (!candidateMatch.Success)
            {
                continue;
            }

            var segment = candidateMatch.Groups["segment"].Success
                ? int.Parse(candidateMatch.Groups["segment"].Value)
                : 0;

            if (!segments.TryAdd(segment, Path.GetFullPath(candidate)))
            {
                throw new InputValidationException(
                    $"Duplicate segment {segment} was found for family '{basePath}'.");
            }
        }

        return ValidateFamily(basePath, segments);
    }

    private static InputFamily ValidateFamily(string basePath, IReadOnlyDictionary<int, string> segments)
    {
        if (!segments.ContainsKey(0))
        {
            throw new InputValidationException($"Family is missing its base segment: '{basePath}'.");
        }

        var ordered = segments.OrderBy(pair => pair.Key).ToArray();
        for (var expected = 0; expected < ordered.Length; expected++)
        {
            if (ordered[expected].Key != expected)
            {
                throw new InputValidationException(
                    $"Family '{basePath}' is missing segment {expected}.");
            }
        }

        return new InputFamily(basePath, ordered.Select(pair => pair.Value).ToArray());
    }

    [GeneratedRegex(
        "^(?<family>.+\\.bin)(?:\\.(?<segment>[1-9]\\d*))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex FamilyFileNameRegex();
}
