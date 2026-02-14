namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Abstract base for dataref references. Holds the X-Plane dataref path.
/// </summary>
public abstract class SimDataRefBase
{
    /// <summary>X-Plane dataref path (e.g. "sim/cockpit/autopilot/heading", "AirbusFBW/Foo[7]").</summary>
    public string DataRef { get; init; } = "";
}

/// <summary>
/// Numeric dataref reference. Holds the dataref path and the latest float value.
/// </summary>
public sealed class SimDataRef : SimDataRefBase
{
    /// <summary>Last received numeric value (updated by the connector on subscription callbacks).</summary>
    public float Value { get; set; }
}

/// <summary>
/// String/data-type dataref reference. Holds the dataref path and the latest string value.
/// </summary>
public sealed class SimStringDataRef : SimDataRefBase
{
    /// <summary>Last received string value (base64-decoded from X-Plane, updated on subscription callbacks).</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// Lightweight command reference.
/// Holds the command path and an optional description.
/// </summary>
public sealed class SimCommand
{
    /// <summary>X-Plane command path (e.g. "sim/autopilot/heading_up").</summary>
    public string Command { get; init; } = "";

    /// <summary>Optional human-readable description.</summary>
    public string Description { get; init; } = "";

    public SimCommand() { }

    public SimCommand(string command, string description = "")
    {
        Command = command;
        Description = description;
    }
}
