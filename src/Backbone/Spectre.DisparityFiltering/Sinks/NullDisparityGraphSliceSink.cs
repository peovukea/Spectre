namespace Spectre.DisparityFiltering.Sinks;

/// <summary>Accepts and discards disparity graph slices.</summary>
public sealed class NullDisparityGraphSliceSink : IDisparityGraphSliceSink
{
    /// <inheritdoc />
    public void Write(DisparityGraphSlice slice)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
