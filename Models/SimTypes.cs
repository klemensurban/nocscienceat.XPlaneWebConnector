namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Abstract base for dataref references. Holds the X-Plane dataref path.
/// </summary>
internal abstract record DataRef(string DataRefPath)
{
    private DataRef() : this("") { }

    /// <summary>
    /// Numeric dataref reference. Holds the X-Plane dataref path for a numeric (float) dataref.
    /// </summary>
    internal sealed record Float(string DataRefPath) : DataRef(DataRefPath);

    /// <summary>
    /// String/data-type dataref reference. Holds the X-Plane dataref path for a string/data dataref.
    /// </summary>
    internal sealed record String(string DataRefPath) : DataRef(DataRefPath);
}

/// <summary>
/// Lightweight command reference.
/// Holds the command path and an optional description.
/// </summary>
public sealed class SimCommand
{
    /// <summary>X-Plane command path (e.g. "sim/autopilot/heading_up").</summary>
    public string Command { get; init; } = "";

}
