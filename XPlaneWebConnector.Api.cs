using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Models;
using System.Text.Json;

namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Stateless <see cref="IXPlaneApi"/> implementations — pure REST and WebSocket sends
/// with no internal subscription state access. Grouped by logical function:
/// capabilities, datarefs (query / write / subscribe), commands (query / activate / subscribe),
/// and flight management.
/// </summary>
public sealed partial class XPlaneWebConnector
{
    // ========================================================================
    // REST: Capabilities
    // ========================================================================

    public async Task<XPlaneCapabilitiesResponse?> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var response = await CreateHttpClient().GetAsync(_capabilitiesUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneCapabilitiesResponse, ct);
    }

    // ========================================================================
    // REST: Dataref queries
    // ========================================================================

    public async Task<List<XPlaneDataRefInfo>> ListDataRefsAsync(
        IEnumerable<string>? filterNames = null,
        int? start = null,
        int? limit = null,
        string? fields = null,
        CancellationToken ct = default)
    {
        var url = BuildQueryUrl($"{_baseUrl}/datarefs", filterNames, start, limit, fields);
        using var response = await CreateHttpClient().GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo, ct);
        return result?.Data ?? [];
    }

    public async Task<int> GetDataRefCountAsync(CancellationToken ct = default)
    {
        using var response = await CreateHttpClient().GetAsync($"{_baseUrl}/datarefs/count", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneScalarResponseInt32, ct);
        return result?.Data ?? 0;
    }

    public async Task<JsonElement> GetDataRefValueAsync(long id, int? index = null, CancellationToken ct = default)
    {
        var indexParam = index.HasValue ? $"?index={index.Value}" : "";
        using var response = await CreateHttpClient().GetAsync($"{_baseUrl}/datarefs/{id}/value{indexParam}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneValueResponse, ct);
        return result?.Data ?? default;
    }

    // ========================================================================
    // REST: Dataref writes
    // ========================================================================

    public async Task SetDataRefValueByHttpAsync(long id, JsonElement value, int? index = null, CancellationToken ct = default)
    {
        var body = new DataRefValueBody { Data = value };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, XPlaneJsonContext.Default.DataRefValueBody);
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var indexParam = index.HasValue ? $"?index={index.Value}" : "";
        if (_fireForgetOnHttpTransport)
        {
            _ = FireAndForgetAsync();
            return;

            async Task FireAndForgetAsync()
            {
                try
                {
                    using var response = await SendAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set dataRef value via HTTP for id {Id}{Index}", id, index.HasValue ? $"[index={index.Value}]" : "");
                }
            }
        }

        using var response = await SendAsync();
        response.EnsureSuccessStatusCode();

        Task<HttpResponseMessage> SendAsync() => CreateHttpClient().PatchAsync($"{_baseUrl}/datarefs/{id}/value{indexParam}", content, ct);
    }

    // ========================================================================
    // WebSocket: Dataref set values
    // ========================================================================

    public async Task SetDataRefValuesByWsAsync(IEnumerable<DataRefSetEntry> datarefs)
    {
        var request = new WsRequest<DataRefSetValuesParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_set_values",
            Params = new DataRefSetValuesParams { Datarefs = [.. datarefs] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestDataRefSetValuesParams);
    }

    // ========================================================================
    // WebSocket: Dataref subscriptions
    // ========================================================================

    public async Task SubscribeDataRefsAsync(IEnumerable<DataRefSubscribeEntry> datarefs)
    {
        var request = new WsRequest<DataRefSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_subscribe_values",
            Params = new DataRefSubscribeParams { Datarefs = [.. datarefs] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestDataRefSubscribeParams);
    }

    public async Task UnsubscribeDataRefsAsync(IEnumerable<DataRefSubscribeEntry> datarefs)
    {
        var request = new WsRequest<DataRefSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_unsubscribe_values",
            Params = new DataRefSubscribeParams { Datarefs = [.. datarefs] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestDataRefSubscribeParams);
    }

    // ========================================================================
    // REST: Command queries
    // ========================================================================

    public async Task<List<XPlaneCommandInfo>> ListCommandsAsync(
        IEnumerable<string>? filterNames = null,
        int? start = null,
        int? limit = null,
        string? fields = null,
        CancellationToken ct = default)
    {
        var url = BuildQueryUrl($"{_baseUrl}/commands", filterNames, start, limit, fields);
        using var response = await CreateHttpClient().GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneCommandInfo, ct);
        return result?.Data ?? [];
    }

    public async Task<int> GetCommandCountAsync(CancellationToken ct = default)
    {
        using var response = await CreateHttpClient().GetAsync($"{_baseUrl}/commands/count", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneScalarResponseInt32, ct);
        return result?.Data ?? 0;
    }

    // ========================================================================
    // REST: Command activation
    // ========================================================================

    public async Task ActivateCommandAsync(long id, float duration = 0, CancellationToken ct = default)
    {
        var body = new CommandActivateBody { Duration = duration };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, XPlaneJsonContext.Default.CommandActivateBody);
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (_fireForgetOnHttpTransport)
        {
            _ = FireAndForgetAsync();
            return;

            async Task FireAndForgetAsync()
            {
                try
                {
                    using var response = await SendAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to activate command via HTTP for id {Id}", id);
                }

            }
        }

        using var response = await SendAsync();
        response.EnsureSuccessStatusCode();

        Task<HttpResponseMessage> SendAsync() => CreateHttpClient().PostAsync($"{_baseUrl}/command/{id}/activate", content);
    }

    // ========================================================================
    // WebSocket: Command activation
    // ========================================================================

    public async Task SetCommandActiveAsync(long id, bool isActive, float? duration = null)
    {
        var request = new WsRequest<CommandSetActiveParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_set_is_active",
            Params = new CommandSetActiveParams
            {
                Commands = [new CommandSetEntry { Id = id, IsActive = isActive, Duration = duration }]
            }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSetActiveParams);
    }

    // ========================================================================
    // WebSocket: Command subscriptions
    // ========================================================================

    public async Task SubscribeCommandUpdatesAsync(IEnumerable<long> commandIds)
    {
        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_subscribe_is_active",
            Params = new CommandSubscribeParams { Commands = [.. commandIds.Select(id => new CommandIdEntry { Id = id })] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    public async Task UnsubscribeCommandUpdatesAsync(IEnumerable<long> commandIds)
    {
        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_unsubscribe_is_active",
            Params = new CommandSubscribeParams { Commands = [.. commandIds.Select(id => new CommandIdEntry { Id = id })] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    // ========================================================================
    // REST: Flight management
    // ========================================================================

    public async Task StartFlightAsync(JsonElement flightData, CancellationToken ct = default)
    {
        var body = new FlightBody { Data = flightData };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, XPlaneJsonContext.Default.FlightBody);
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (_fireForgetOnHttpTransport)
        {
            _ = FireAndForgetAsync();
            return;

            async Task FireAndForgetAsync()
            {
                try
                {
                    using var response = await SendAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed StartFlightAsync");
                }
            }
        }

        using var response = await SendAsync();
        response.EnsureSuccessStatusCode();

        Task<HttpResponseMessage> SendAsync() => CreateHttpClient().PostAsync($"{_baseUrl}/flight", content, ct);
    }

    public async Task UpdateFlightAsync(JsonElement flightData, CancellationToken ct = default)
    {
        var body = new FlightBody { Data = flightData };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, XPlaneJsonContext.Default.FlightBody);
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (_fireForgetOnHttpTransport)
        {
            _ = FireAndForgetAsync();
            return;

            async Task FireAndForgetAsync()
            {
                try
                {
                    using var response = await SendAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed UpdateFlightAsync");
                }
            }
        }

        using var response = await SendAsync();
        response.EnsureSuccessStatusCode();

        Task<HttpResponseMessage> SendAsync() => CreateHttpClient().PatchAsync($"{_baseUrl}/flight", content, ct);
    }

    // ========================================================================
    // REST query URL builder
    // ========================================================================

    private static string BuildQueryUrl(string baseUrl, IEnumerable<string>? filterNames, int? start, int? limit, string? fields)
    {
        var parts = new List<string>();
        if (filterNames is not null)
        {
            foreach (var name in filterNames)
                parts.Add($"filter[name]={Uri.EscapeDataString(name)}");
        }
        if (start.HasValue) parts.Add($"start={start.Value}");
        if (limit.HasValue) parts.Add($"limit={limit.Value}");
        if (!string.IsNullOrEmpty(fields)) parts.Add($"fields={Uri.EscapeDataString(fields)}");

        return parts.Count > 0 ? $"{baseUrl}?{string.Join('&', parts)}" : baseUrl;
    }
}
