using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Availability check methods: poll the X-Plane REST API and optionally
/// wait for a plugin dataRef to be registered before proceeding.
/// </summary>
public sealed partial class XPlaneWebConnector
{
    // ========================================================================
    // Availability check
    // ========================================================================

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await CreateHttpClient().GetAsync(_capabilitiesUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var caps = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneCapabilitiesResponse, cancellationToken);
            return caps?.Api?.Versions is { Count: > 0 };
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task WaitUntilAvailableAsync(TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);

        // Phase 1: Wait for X-Plane web server to respond
        _logger.LogInformation("Waiting for X-Plane API at {Url} ...", _capabilitiesUrl);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await IsAvailableAsync(cancellationToken))
            {
                _logger.LogInformation("X-Plane API is available");
                break;
            }

            _logger.LogDebug("X-Plane API not yet available, retrying in {Interval}s ...", interval.TotalSeconds);
            await Task.Delay(interval, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: Wait for aircraft plugin datarefs to be registered (if configured)
        if (string.IsNullOrEmpty(_readinessProbeDataRef))
            return;

        _logger.LogInformation("Waiting for plugin dataRef '{DataRefPath}' to become available ...", _readinessProbeDataRef);

        var probeUrl = $"{_baseUrl}/datarefs?filter[name]={Uri.EscapeDataString(_readinessProbeDataRef)}&fields=id";
        int attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                using var response = await CreateHttpClient().GetAsync(probeUrl, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var result = await JsonSerializer.DeserializeAsync(
                        stream, Models.XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo, cancellationToken);

                    if (result?.Data is { Count: > 0 })
                    {
                        _logger.LogInformation("Plugin dataRef '{DataRefPath}' is available (after {Attempts} attempt(s))",
                            _readinessProbeDataRef, attempt);
                        return;
                    }
                }
            }
            catch (HttpRequestException) { /* server may have restarted during aircraft load */ }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) { /* timeout */ }

            if (_readinessProbeMaxRetries > 0 && attempt >= _readinessProbeMaxRetries)
            {
                _logger.LogWarning("Plugin dataRef '{DataRefPath}' not available after {Max} retries, continuing anyway",
                    _readinessProbeDataRef, _readinessProbeMaxRetries);
                return;
            }

            _logger.LogDebug("Plugin dataRef '{DataRefPath}' not yet available (attempt {Attempt}{MaxInfo}), retrying in {Interval}s ...",
                _readinessProbeDataRef, attempt,
                _readinessProbeMaxRetries > 0 ? $"/{_readinessProbeMaxRetries}" : "",
                interval.TotalSeconds);
            await Task.Delay(interval, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
