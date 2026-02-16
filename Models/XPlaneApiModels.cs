using System.Text.Json;
using System.Text.Json.Serialization;

namespace nocscienceat.XPlaneWebConnector;

// ========================================================================
// Command transport selection
// ========================================================================

/// <summary>
/// Determines how fire-and-forget commands are sent to X-Plane.
/// </summary>
public enum CommandSetDataRefTransport
{
    /// <summary>Send commands via WebSocket (command_set_is_active). Default.</summary>
    WebSocket,

    /// <summary>Send commands via HTTP POST /command/{id}/activate.</summary>
    Http
}

// ========================================================================
// REST response models
// ========================================================================

/// <summary>Wrapper for X-Plane REST API list responses: { "data": [...] }</summary>
public sealed class XPlaneListResponse<T>
{
    public List<T> Data { get; init; } = [];
}

/// <summary>Wrapper for X-Plane REST API scalar responses: { "data": T }</summary>
public sealed class XPlaneScalarResponse<T>
{
    public T Data { get; init; } = default!;
}

/// <summary>Wrapper for value responses where data can be any JSON type.</summary>
public sealed class XPlaneValueResponse
{
    public JsonElement Data { get; init; }
}

/// <summary>Dataref metadata returned by GET /datarefs.</summary>
public sealed class XPlaneDataRefInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string ValueType { get; init; } = "";
}

/// <summary>Command metadata returned by GET /commands.</summary>
public sealed class XPlaneCommandInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Body for PATCH /datarefs/{id}/value (generic JSON value).</summary>
public sealed class DataRefValueBody
{
    public JsonElement Data { get; init; }
}

/// <summary>Body for POST /command/{id}/activate.</summary>
public sealed class CommandActivateBody
{
    public float Duration { get; init; }
}

/// <summary>Body for POST /flight and PATCH /flight.</summary>
public sealed class FlightBody
{
    public JsonElement Data { get; init; }
}

/// <summary>Response from GET /api/capabilities (unversioned endpoint).</summary>
public sealed class XPlaneCapabilitiesResponse
{
    public XPlaneApiCapabilities? Api { get; init; }

    [JsonPropertyName("x-plane")]
    public XPlaneVersionInfo? XPlane { get; init; }
}

public sealed class XPlaneApiCapabilities
{
    public List<string> Versions { get; init; } = [];
}

public sealed class XPlaneVersionInfo
{
    public string Version { get; init; } = "";
}

// ========================================================================
// WebSocket outgoing request models
// ========================================================================

/// <summary>Generic WebSocket request envelope: { "req_id", "type", "params" }.</summary>
public sealed class WsRequest<TParams>
{
    public int ReqId { get; init; }
    public string Type { get; init; } = "";
    public TParams? Params { get; init; }
}

/// <summary>Params for type "dataref_subscribe_values" and "dataref_unsubscribe_values".</summary>
public sealed class DataRefSubscribeParams
{
    public List<DataRefSubscribeEntry> Datarefs { get; init; } = [];
}

/// <summary>
/// Entry for dataref subscribe/unsubscribe requests.
/// Per the X-Plane API spec, <see cref="Index"/> can be a single int, an array of ints,
/// or omitted (null) to subscribe to all indices / a scalar dataref.
/// </summary>
public sealed class DataRefSubscribeEntry
{
    public long Id { get; init; }
    public JsonElement? Index { get; init; }
}

/// <summary>Params for "dataref_unsubscribe_values" with the "all" shorthand.</summary>
public sealed class DataRefUnsubscribeAllParams
{
    public string Datarefs { get; init; } = "all";
}

/// <summary>Params for type "dataref_set_values".</summary>
public sealed class DataRefSetValuesParams
{
    public List<DataRefSetEntry> Datarefs { get; init; } = [];
}

public sealed class DataRefSetEntry
{
    public long Id { get; init; }
    public JsonElement Value { get; init; }
    public int? Index { get; init; }
}

/// <summary>Params for "command_set_is_active".</summary>
public sealed class CommandSetActiveParams
{
    public List<CommandSetEntry> Commands { get; init; } = [];
}

public sealed class CommandSetEntry
{
    public long Id { get; init; }
    public bool IsActive { get; init; }
    public float? Duration { get; init; }
}

/// <summary>Params for "command_subscribe_is_active" and "command_unsubscribe_is_active".</summary>
public sealed class CommandSubscribeParams
{
    public List<CommandIdEntry> Commands { get; init; } = [];
}

public sealed class CommandIdEntry
{
    public long Id { get; init; }
}

/// <summary>Params for "command_unsubscribe_is_active" with the "all" shorthand.</summary>
public sealed class CommandUnsubscribeAllParams
{
    public string Commands { get; init; } = "all";
}

// ========================================================================
// WebSocket incoming message models
// ========================================================================

/// <summary>
/// WebSocket "result" message from X-Plane, sent in response to
/// subscribe, set-values, or command requests.
/// </summary>
public sealed class WsResultMessage
{
    public int ReqId { get; init; }
    public string Type { get; init; } = "";
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

// NOTE: "dataref_update_values" and "command_update_is_active" messages have
// dynamic keys (IDs as strings). These are parsed with JsonDocument directly.

// ========================================================================
// Source-generated JSON serializer context
// ========================================================================

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(XPlaneListResponse<XPlaneDataRefInfo>))]
[JsonSerializable(typeof(XPlaneListResponse<XPlaneCommandInfo>))]
[JsonSerializable(typeof(XPlaneScalarResponse<int>))]
[JsonSerializable(typeof(XPlaneValueResponse))]
[JsonSerializable(typeof(WsRequest<DataRefSubscribeParams>))]
[JsonSerializable(typeof(WsRequest<DataRefUnsubscribeAllParams>))]
[JsonSerializable(typeof(WsRequest<DataRefSetValuesParams>))]
[JsonSerializable(typeof(WsRequest<CommandSetActiveParams>))]
[JsonSerializable(typeof(WsRequest<CommandSubscribeParams>))]
[JsonSerializable(typeof(WsRequest<CommandUnsubscribeAllParams>))]
[JsonSerializable(typeof(WsResultMessage))]
[JsonSerializable(typeof(DataRefValueBody))]
[JsonSerializable(typeof(CommandActivateBody))]
[JsonSerializable(typeof(FlightBody))]
[JsonSerializable(typeof(XPlaneCapabilitiesResponse))]
public partial class XPlaneJsonContext : JsonSerializerContext;
