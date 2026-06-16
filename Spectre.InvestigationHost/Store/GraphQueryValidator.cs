namespace Spectre.InvestigationHost.Store;

public sealed record GraphQueryValidationResult(bool IsValid, string? Error)
{
    public static GraphQueryValidationResult Valid { get; } = new(true, null);
    public static GraphQueryValidationResult Invalid(string error) => new(false, error);
}

public static class GraphQueryValidator
{
    public static GraphQueryValidationResult Validate(
        GraphQueryParameters parameters,
        IReadOnlySet<string> predicates,
        IReadOnlySet<string> nodeKinds)
    {
        if (!double.IsFinite(parameters.MinWeight) || parameters.MinWeight < 0)
        {
            return GraphQueryValidationResult.Invalid("minWeight must be a finite number greater than or equal to 0.");
        }

        if (parameters.MaxNodes is < 2 or > 1000)
        {
            return GraphQueryValidationResult.Invalid("maxNodes must be between 2 and 1000.");
        }

        if (parameters.MaxEdges is < 1 or > 2000)
        {
            return GraphQueryValidationResult.Invalid("maxEdges must be between 1 and 2000.");
        }

        if (parameters.Predicate is { Length: > 0 } predicate && !predicates.Contains(predicate))
        {
            return GraphQueryValidationResult.Invalid($"Unknown predicate '{predicate}'.");
        }

        if (parameters.NodeKind is { Length: > 0 } nodeKind && !nodeKinds.Contains(nodeKind))
        {
            return GraphQueryValidationResult.Invalid($"Unknown node kind '{nodeKind}'.");
        }

        return GraphQueryValidationResult.Valid;
    }
}
