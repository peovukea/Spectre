namespace Spectre.SemanticIndexing.Metadata;

internal sealed class NodeMetadata
{
    private readonly Dictionary<string, string> _attributes =
        new(StringComparer.OrdinalIgnoreCase);

    public void Set(string predicate, string value) => _attributes[predicate] = value;

    public string? Resolve(params string[] canonicalSynonyms)
    {
        foreach (var prefix in new[] { "", "HAS_PROPERTY_", "HAS_BASE_PROPERTY_" })
        {
            foreach (var canonical in canonicalSynonyms)
            {
                var suffix = canonical["HAS_".Length..];
                var predicate = prefix.Length == 0 ? canonical : prefix + suffix;
                if (_attributes.TryGetValue(predicate, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
