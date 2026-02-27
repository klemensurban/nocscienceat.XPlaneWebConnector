using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Models;
using System.Text.Json;

namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Standalone <see cref="IXPlaneApi"/> implementations that are not called
/// by the high-level <see cref="IXPlaneWebConnector"/> surface.
/// These provide direct REST access for tooling, debugging, and flight management.
/// </summary>
public sealed partial class XPlaneWebConnector
{
    // ========================================================================
    // REST: Capabilities
    // ========================================================================

    public async Task<XPlaneCapabilitiesResponse?> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var response = await _httpClientFactory.CreateClient().GetAsync(_capabilitiesUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneCapabilitiesResponse, ct);
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
        using var response = await _httpClientFactory.CreateClient().GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo, ct);
        return result?.Data ?? [];
    }

    public async Task<int> GetDataRefCountAsync(CancellationToken ct = default)
    {
        using var response = await _httpClientFactory.CreateClient().GetAsync($"{_baseUrl}/datarefs/count", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneScalarResponseInt32, ct);
        return result?.Data ?? 0;
    }

    public async Task<JsonElement> GetDataRefValueAsync(long id, int? index = null, CancellationToken ct = default)
    {
        var indexParam = index.HasValue ? $"?index={index.Value}" : "";
        using var response = await _httpClientFactory.CreateClient().GetAsync($"{_baseUrl}/datarefs/{id}/value{indexParam}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneValueResponse, ct);
        return result?.Data ?? default;
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
        using var response = await _httpClientFactory.CreateClient().GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneListResponseXPlaneCommandInfo, ct);
        return result?.Data ?? [];
    }

    public async Task<int> GetCommandCountAsync(CancellationToken ct = default)
    {
        using var response = await _httpClientFactory.CreateClient().GetAsync($"{_baseUrl}/commands/count", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneScalarResponseInt32, ct);
        return result?.Data ?? 0;
    }

    // ========================================================================
    // REST: Flight management
    // ========================================================================

    public async Task StartFlightAsync(JsonElement flightData, CancellationToken ct = default)
    {
        var body = new FlightBody { Data = flightData };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Models.XPlaneJsonContext.Default.FlightBody);
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

        Task<HttpResponseMessage> SendAsync() => _httpClientFactory.CreateClient().PostAsync($"{_baseUrl}/flight", content, ct);
    }

    public async Task UpdateFlightAsync(JsonElement flightData, CancellationToken ct = default)
    {
        var body = new FlightBody { Data = flightData };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Models.XPlaneJsonContext.Default.FlightBody);
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

        Task<HttpResponseMessage> SendAsync() => _httpClientFactory.CreateClient().PatchAsync($"{_baseUrl}/flight", content, ct);
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
