namespace Spectre.Backbone.Api.Sinks;

/// <summary>Receives closed disparity-filtered graph slices.</summary>
public interface IDisparityGraphSliceSink : IDisposable
{
    /// <summary>Accepts one closed disparity graph slice.</summary>
    void Write(DisparityGraphSlice slice);
}
