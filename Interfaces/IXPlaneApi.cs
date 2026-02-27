using System.Text.Json;
using nocscienceat.XPlaneWebConnector.Models;

namespace nocscienceat.XPlaneWebConnector.Interfaces;

/// <summary>
/// Full X-Plane 12.1.1+ REST and WebSocket API surface.
/// Covers all operations defined in the X-Plane web API specification.
/// This interface serves as living documentation of the complete X-Plane API
/// and is not intended to be consumed outside the library. Use
/// <see cref="IXPlaneWebConnector"/> as the public-facing contract instead.
/// </summary>
internal interface IXPlaneApi
{
    // ========================================================================
    // REST: Capabilities
    // ========================================================================

    /// <summary>GET /api/capabilities — Returns supported API versions and X-Plane version.</summary>
    Task<XPlaneCapabilitiesResponse?> GetCapabilitiesAsync(CancellationToken ct = default);

    // ========================================================================
    // REST: Datarefs
    // ========================================================================

    /// <summary>GET /datarefs — Returns registered datarefs, with optional filtering and pagination.</summary>
    Task<List<XPlaneDataRefInfo>> ListDataRefsAsync(
        IEnumerable<string>? filterNames = null,
        int? start = null,
        int? limit = null,
        string? fields = null,
        CancellationToken ct = default);

    /// <summary>GET /datarefs/count — Returns the count of registered datarefs.</summary>
    Task<int> GetDataRefCountAsync(CancellationToken ct = default);

    /// <summary>GET /datarefs/{id}/value — Returns the current value of a dataref.</summary>
    Task<JsonElement> GetDataRefValueAsync(long id, int? index = null, CancellationToken ct = default);

    /// <summary>PATCH /datarefs/{id}/value — Sets a dataref value via REST.</summary>
    Task SetDataRefValueByIdAsync(long id, JsonElement value, int? index = null, CancellationToken ct = default);

    /// <summary>WebSocket dataref_set_values — Sets one or more dataref values via WebSocket (faster than REST for frequent updates).</summary>
    Task SetDataRefValuesByWsAsync(IEnumerable<DataRefSetEntry> datarefs);

    // ========================================================================
    // REST: Commands
    // ========================================================================

    /// <summary>GET /commands — Returns registered commands, with optional filtering and pagination.</summary>
    Task<List<XPlaneCommandInfo>> ListCommandsAsync(
        IEnumerable<string>? filterNames = null,
        int? start = null,
        int? limit = null,
        string? fields = null,
        CancellationToken ct = default);

    /// <summary>GET /commands/count — Returns the count of registered commands.</summary>
    Task<int> GetCommandCountAsync(CancellationToken ct = default);

    /// <summary>POST /command/{id}/activate — Activates a command for a fixed duration (REST fire-and-forget).</summary>
    Task ActivateCommandAsync(long id, float duration = 0, CancellationToken ct = default);

    // ========================================================================
    // REST: Flight
    // ========================================================================

    /// <summary>POST /flight — Starts a new flight with the given initialization data.</summary>
    Task StartFlightAsync(JsonElement flightData, CancellationToken ct = default);

    /// <summary>PATCH /flight — Updates the current flight configuration.</summary>
    Task UpdateFlightAsync(JsonElement flightData, CancellationToken ct = default);

    // ========================================================================
    // WebSocket: Dataref subscriptions
    // NOTE: These are low-level WebSocket messages. At runtime, subscriptions
    // are routed through the Worker's command channel for thread safety.
    // Kept here to document the full X-Plane API surface.
    // ========================================================================

    /// <summary>Subscribes to one or more dataref value updates via WebSocket.</summary>
    Task SubscribeDataRefsAsync(IEnumerable<DataRefSubscribeEntry> datarefs);

    /// <summary>Unsubscribes from one or more dataref value updates.</summary>
    Task UnsubscribeDataRefsAsync(IEnumerable<DataRefSubscribeEntry> datarefs);

    /// <summary>Unsubscribes from all dataref value updates.</summary>
    Task UnsubscribeAllDataRefsAsync();

    // ========================================================================
    // WebSocket: Command subscriptions
    // NOTE: These are low-level WebSocket messages. At runtime, subscriptions
    // are routed through the Worker's command channel for thread safety.
    // Kept here to document the full X-Plane API surface.
    // ========================================================================

    /// <summary>Subscribes to command activation status updates via WebSocket.</summary>
    Task SubscribeCommandUpdatesAsync(IEnumerable<long> commandIds);

    /// <summary>Unsubscribes from one or more command activation updates via WebSocket.</summary>
    Task UnsubscribeCommandUpdatesAsync(IEnumerable<long> commandIds);

    /// <summary>Unsubscribes from all command activation updates.</summary>
    Task UnsubscribeAllCommandUpdatesAsync();

    // ========================================================================
    // WebSocket: Command activation
    // ========================================================================

    /// <summary>
    /// Activates or deactivates a command via WebSocket.
    /// Supports optional duration for timed activation.
    /// </summary>
    Task SetCommandActiveAsync(long id, bool isActive, float? duration = null);
}
