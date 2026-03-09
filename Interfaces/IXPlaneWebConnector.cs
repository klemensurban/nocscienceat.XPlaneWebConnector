using nocscienceat.XPlaneWebConnector.Models;

namespace nocscienceat.XPlaneWebConnector.Interfaces;

/// <summary>
/// Abstraction for communicating with X-Plane.
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
    // Availability check
    // ========================================================================

    /// <summary>Returns true if the X-Plane REST API is reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks until the X-Plane API is reachable and (optionally) a readiness-probe
    /// dataref is registered. Useful to wait for aircraft plugins to finish loading.
    /// </summary>
    Task WaitUntilAvailableAsync(TimeSpan? pollInterval = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when the WebSocket connection to X-Plane is closed cleanly
    /// (e.g. X-Plane exited). Subscribers can use this to trigger a graceful
    /// application shutdown.
    /// </summary>
    event Action? ConnectionClosed;

    // ========================================================================
    // DataRefPath subscriptions
    // ========================================================================

    /// <summary>Subscribes to a numeric dataRef. Dispose the returned handle to unsubscribe.</summary>
    Task<IDisposable> SubscribeAsync(string dataRefPath, Action<float> onchange);

    Task<IDisposable> SubscribeAsync(string dataRefPath, Action<int> onchange);

    Task<IDisposable> SubscribeAsync(string dataRefPath, Action<double> onchange);

    /// <summary>Subscribes to a string/data-type dataRef. Dispose the returned handle to unsubscribe.</summary>
    Task<IDisposable> SubscribeAsync(string dataRefPath, Action<string> onchange);

    // ========================================================================
    // DataRefPath writes
    // ========================================================================

    /// <summary>Sets a numeric dataRefPath value by path.</summary>
    Task SetDataRefValueAsync(string dataRefPath, float value);
    Task SetDataRefValueAsync(string dataRefPath, int value);
    Task SetDataRefValueAsync(string dataRefPath, double value);

    /// <summary>Sets a string dataRefPath value by path.</summary>
    Task SetDataRefValueAsync(string dataRefPath, string value);

    // ========================================================================
    // Commands
    // ========================================================================

    /// <summary>Sends a one-shot command to X-Plane.</summary>
    Task SendCommandAsync(string commandPath);

    /// <summary>
    /// Sends a command to X-Plane with a specific hold duration (0–10 seconds).
    /// When using <see cref="CommandTransport.HttpPost"/>, this maps directly to
    /// POST /command/{id}/activate with the given duration.
    /// When using <see cref="CommandTransport.WebSocket"/>, a duration of 0 sends
    /// a single active+release; a non-zero duration is passed to command_set_is_active.
    /// </summary>
    Task SendCommandAsync(string commandPath, float duration);

    // ========================================================================
    // Command activation subscriptions
    // ========================================================================

    /// <summary>
    /// Subscribes to command activation status updates.
    /// Dispose the returned handle to unsubscribe this consumer;
    /// X-Plane is unsubscribed only when the last consumer disposes.
    /// </summary>
    Task<IDisposable> SubscribeCommandAsync(string commandPath, Action<bool> onUpdate);
}

