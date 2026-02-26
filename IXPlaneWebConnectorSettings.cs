namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Abstraction for <see cref="XPlaneWebConnector"/> configuration.
/// Implement in consumer projects to enable clean DI registration.
/// </summary>
public interface IXPlaneWebConnectorSettings
{
    /// <summary>X-Plane host address.</summary>
    string Host { get; }

    /// <summary>X-Plane REST/WebSocket API port.</summary>
    int Port { get; }

    /// <summary>
    /// Transport for outbound commands and dataref writes: <c>"WebSocket"</c> or <c>"Http"</c>.
    /// </summary>
    string Transport { get; }

    /// <summary>
    /// When using HTTP transport, fire-and-forget write operations (no await on response).
    /// </summary>
    bool FireForgetOnHttpTransport { get; }

    /// <summary>
    /// X-Plane REST/WebSocket API version path segment (e.g. <c>"v2"</c>, <c>"v3"</c>).
    /// </summary>
    string ApiVersion { get; }

    /// <summary>
    /// Optional dataref path to probe during readiness check.
    /// </summary>
    string? ReadinessProbeDataRef { get; }

    /// <summary>
    /// Maximum readiness probe retries. <c>0</c> = unlimited.
    /// </summary>
    int ReadinessProbeMaxRetries { get; }
}
