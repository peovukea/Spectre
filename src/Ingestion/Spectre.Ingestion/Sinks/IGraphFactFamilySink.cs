namespace Spectre.Ingestion.Sinks;

/// <summary>
/// Optional graph-fact sink lifecycle used to isolate state by logical input family.
/// </summary>
public interface IGraphFactFamilySink : IGraphFactSink
{
    /// <summary>Notifies the sink that a logical input family is starting.</summary>
    /// <param name="familyBasePath">Absolute path of the family's base segment.</param>
    void BeginFamily(string familyBasePath);

    /// <summary>Notifies the sink that a logical input family completed successfully.</summary>
    /// <param name="familyBasePath">Absolute path of the family's base segment.</param>
    void EndFamily(string familyBasePath);
}
