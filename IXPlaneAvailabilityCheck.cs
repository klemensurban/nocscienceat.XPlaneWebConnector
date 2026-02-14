namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Checks whether X-Plane's web API is available and ready.
/// Implemented by <see cref="XPlaneWebConnector"/>.
/// </summary>
public interface IXPlaneAvailabilityCheck
{
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
}
