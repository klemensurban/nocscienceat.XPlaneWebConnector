namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Configuration settings for <see cref="XPlaneWebConnector"/>.
/// Bind from <c>IConfiguration</c> section (default key: <c>"XPlaneWebConnector"</c>).
/// </summary>
public class XPlaneWebConnectorSettings : IXPlaneWebConnectorSettings
{
    /// <summary>X-Plane host address. Default: <c>"127.0.0.1"</c>.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>X-Plane REST/WebSocket API port. Default: <c>8086</c>.</summary>
    public int Port { get; set; } = 8086;

    /// <summary>
    /// Transport for outbound commands and dataref writes: <c>"WebSocket"</c> or <c>"Http"</c>.
    /// Default: <c>"WebSocket"</c>.
    /// </summary>
    public string Transport { get; set; } = "WebSocket";

    /// <summary>
    /// When using HTTP transport, fire-and-forget write operations (no await on response).
    /// Default: <c>true</c>.
    /// </summary>
    public bool FireForgetOnHttpTransport { get; set; } = true;

    /// <summary>
    /// X-Plane REST/WebSocket API version path segment (e.g. <c>"v2"</c>, <c>"v3"</c>).
    /// Default: <c>"v2"</c>.
    /// </summary>
    public string ApiVersion { get; set; } = "v2";

    /// <summary>
    /// Optional dataref path to probe during readiness check.
    /// When set, <see cref="XPlaneWebConnector.WaitUntilAvailableAsync"/> waits for this
    /// dataref to appear in X-Plane before returning.
    /// </summary>
    public string? ReadinessProbeDataRef { get; set; }

    /// <summary>
    /// Maximum readiness probe retries. <c>0</c> = unlimited.
    /// </summary>
    public int ReadinessProbeMaxRetries { get; set; } = 0;
}
