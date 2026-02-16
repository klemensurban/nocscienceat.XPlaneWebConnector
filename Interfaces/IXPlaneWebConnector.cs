using nocscienceat.XPlaneWebConnector.Models;

namespace nocscienceat.XPlaneWebConnector.Interfaces;

/// <summary>
/// Abstraction for communicating with X-Plane.
/// Implementations: <see cref="XPlaneConnectorAdapter"/> (UDP/NuGet), <see cref="XPlaneWebConnector"/> (REST+WebSocket).
/// </summary>
public interface IXPlaneWebConnector : IDisposable
{
    // ========================================================================
    // Lifecycle
    // ========================================================================

    /// <summary>Starts the connection to X-Plane.</summary>
    void Start();

    /// <summary>Stops the connection to X-Plane.</summary>
    Task StopAsync(int timeout = 5000);

    // ========================================================================
    // DataRef subscriptions
    // ========================================================================

    /// <summary>Subscribes to a numeric dataref.</summary>
    Task SubscribeAsync(SimDataRef dataref, Action<SimDataRef, float>? onchange = null);

    /// <summary>Subscribes to a string/data-type dataref. Values are base64-decoded from X-Plane.</summary>
    Task SubscribeAsync(SimStringDataRef dataref, Action<SimStringDataRef, string>? onchange = null);

    // ========================================================================
    // DataRef writes
    // ========================================================================

    /// <summary>Sets a dataref value using a SimDataRef.</summary>
    Task SetDataRefValueAsync(SimDataRef dataref, float value);

    /// <summary>Sets a string dataref value using a SimStringDataRef.</summary>
    Task SetDataRefValueAsync(SimStringDataRef dataref, string value);

    /// <summary>Sets a numeric dataref value by path.</summary>
    Task SetDataRefValueAsync(string dataref, float value);

    /// <summary>Sets a string dataref value by path.</summary>
    Task SetDataRefValueAsync(string dataref, string value);

    // ========================================================================
    // Commands
    // ========================================================================

    /// <summary>Sends a one-shot command to X-Plane.</summary>
    Task SendCommandAsync(SimCommand command);

    /// <summary>
    /// Sends a command to X-Plane with a specific hold duration (0–10 seconds).
    /// When using <see cref="CommandTransport.HttpPost"/>, this maps directly to
    /// POST /command/{id}/activate with the given duration.
    /// When using <see cref="CommandTransport.WebSocket"/>, a duration of 0 sends
    /// a single active+release; a non-zero duration is passed to command_set_is_active.
    /// </summary>
    Task SendCommandAsync(SimCommand command, float duration);
}

