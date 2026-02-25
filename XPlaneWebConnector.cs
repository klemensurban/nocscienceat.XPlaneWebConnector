using ChannelWorker;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Interfaces;
using nocscienceat.XPlaneWebConnector.Models;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Communicates with X-Plane 12.1.1+ via the built-in REST and WebSocket API.
/// Implements the high-level <see cref="IXPlaneWebConnector"/> surface used by panels,
/// the low-level <see cref="IXPlaneApi"/> surface, and the
/// <see cref="IXPlaneAvailabilityCheck"/> used at startup.
/// 
/// All WebSocket sends are fire-and-forget: X-Plane does not reliably deliver
/// "result" responses while the receive loop is busy dispatching subscription
/// callbacks, so we never block waiting for acknowledgements.
/// </summary>
public sealed partial class XPlaneWebConnector : IXPlaneWebConnector, IXPlaneApi, IXPlaneAvailabilityCheck
{
    private readonly ILogger<XPlaneWebConnector> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CommandSetDataRefTransport _transport;
    private readonly bool _fireForgetOnHttpTransport;
    private readonly string _baseUrl;
    private readonly string _wsUrl;
    private readonly string _capabilitiesUrl;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _callbackTask;
    private Task? _workerTask;

    // Dataref session-ID caches (path ? id). Cleared when X-Plane restarts.
    private readonly ConcurrentDictionary<string, long> _dataRefIdCache = new();
    private readonly ConcurrentDictionary<long, string> _reverseDataRefIdCache = new();
    private readonly ConcurrentDictionary<string, long> _commandIdCache = new();
    private readonly ConcurrentDictionary<long, string> _reverseCommandIdCache = new();

    // Numeric dataRef subscriptions: (sessionId, arrayIndex) ? (element, callbacks)
    private readonly ConcurrentDictionary<(long Id, int Index), (SimDataRef Element, ImmutableArray<Action<SimDataRef>> Callbacks)> _subscriptions = new();

    // String/data dataRef subscriptions: (sessionId, arrayIndex) ? (element, callbacks)
    private readonly ConcurrentDictionary<(long Id, int Index), (SimStringDataRef Element, ImmutableArray<Action<SimStringDataRef>> Callbacks)> _stringSubscriptions = new();

    // Tracks which array indices are subscribed per dataRef ID.
    // X-Plane sends array updates with values for subscribed indices in sorted order;
    // we need this map to correlate array positions back to the original indices.
    private readonly ConcurrentDictionary<long, SortedSet<int>> _subscribedIndices = new();

    // Command activation subscriptions: command session ID ? callbacks
    private readonly ConcurrentDictionary<long, ImmutableArray<Action<long, bool>>> _commandSubscriptions = new();

    // Monotonically increasing request ID for WebSocket messages
    private int _nextReqId;

    // Data channel: WebSocket messages are queued here so the receive loop
    // is never blocked by slow callback processing (e.g. serial port writes).
    // Read by the Worker's HandleData on its dedicated thread.
    private readonly Channel<XPlaneDataMessage> _dataChannel = Channel.CreateBounded<XPlaneDataMessage>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Command channel: subscription commands are sent here and processed
    // by the Worker's HandleCommandAsync with talkback via TaskCompletionSource.
    private readonly Channel<CommandEnvelope<WorkerCommand, bool>> _commandChannel = Channel.CreateUnbounded<CommandEnvelope<WorkerCommand, bool>>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<CallbackItem> _callbacks = Channel.CreateUnbounded<CallbackItem>(new UnboundedChannelOptions { SingleReader = true });

    // Readiness probe configuration
    private readonly string? _readinessProbeDataRef;
    private readonly int _readinessProbeMaxRetries;

    /// <inheritdoc />
    public event Action? ConnectionClosed;

    public XPlaneWebConnector(string host, int port, CommandSetDataRefTransport commandTransport, bool fireForgetOnHttpTransport, ILogger<XPlaneWebConnector> logger, IHttpClientFactory httpClientFactory, 
        string? readinessProbeDataRef = null, int readinessProbeMaxRetries = 0)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _baseUrl = $"http://{host}:{port}/api/v3";
        _wsUrl = $"ws://{host}:{port}/api/v3";
        _capabilitiesUrl = $"http://{host}:{port}/api/capabilities";
        _readinessProbeDataRef = readinessProbeDataRef;
        _readinessProbeMaxRetries = readinessProbeMaxRetries;
        _transport = commandTransport;
        _fireForgetOnHttpTransport = fireForgetOnHttpTransport;
    }

    // ========================================================================
    // Availability check (IXPlaneAvailabilityCheck)
    // ========================================================================

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient().GetAsync(_capabilitiesUrl, cancellationToken);
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
                using var response = await _httpClientFactory.CreateClient().GetAsync(probeUrl, cancellationToken);

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


    // ========================================================================
    // Lifecycle
    // ========================================================================

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _receiveTask = ConnectWebSocketAndReceiveAsync(_cts.Token);
        _callbackTask = StartCallbacksAsync(_cts.Token);

        var handler = new DataRefWorkerHandler(this);
        var worker = new Worker<WorkerCommand, bool, XPlaneDataMessage>(
            _commandChannel, _dataChannel, handler, _logger);
        _workerTask = worker.RunAsync(_cts.Token);

        _logger.LogInformation("XPlaneWebConnector started (REST: {RestUrl}, WS: {WsUrl})", _baseUrl, _wsUrl);
    }

    public async Task StopAsync(int timeout = 5000)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();

            var tasks = new List<Task>();
            if (_receiveTask is not null) tasks.Add(_receiveTask);
            if (_callbackTask is not null) tasks.Add(_callbackTask);
            if (_workerTask is not null) tasks.Add(_workerTask);

            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(timeout)); }
            catch (TimeoutException) { _logger.LogWarning("Background tasks did not stop within {Timeout}ms", timeout); }
            catch (OperationCanceledException) { /* expected */ }
        }

        // The Worker is the sole consumer of subscription state.
        // Await it specifically so the dictionaries are never cleared
        // while HandleData/HandleCommandAsync is still running.
        if (_workerTask is { IsCompleted: false })
        {
            _logger.LogDebug("Awaiting Worker shutdown before clearing subscription state ...");
            try { await _workerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Worker faulted during shutdown"); }
        }

        if (_webSocket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket close error");
            }
        }

        _subscriptions.Clear();
        _stringSubscriptions.Clear();
        _subscribedIndices.Clear();
        _logger.LogInformation("XPlaneWebConnector stopped");
    }

    // ========================================================================
    // IXPlaneWebConnector: Subscribe to dataRef updates (WebSocket)
    // ========================================================================

    public async Task SubscribeAsync(SimDataRef dataRef, Action<SimDataRef>? onchange = null)
    {
        var (basePath, index) = ParseDataRefPath(dataRef.DataRefPath);
        var id = await ResolveDataRefIdAsync(basePath);
        var cb = onchange ?? ((_) => { });

        await _commandChannel.Writer.SendCommandAsync(
            new WorkerCommand.SubscribeNumeric(id, index, dataRef, cb));

        _logger.LogDebug("Subscribed to dataRef {Name} (id={Id}, index={Index})", dataRef.DataRefPath, id, index);
    }

    public async Task SubscribeAsync(SimStringDataRef dataRef, Action<SimStringDataRef>? onchange = null)
    {
        var (basePath, index) = ParseDataRefPath(dataRef.DataRefPath);
        var id = await ResolveDataRefIdAsync(basePath);
        var cb = onchange ?? ((_) => { });

        await _commandChannel.Writer.SendCommandAsync(
            new WorkerCommand.SubscribeString(id, index, dataRef, cb));

        _logger.LogDebug("Subscribed to string dataRef {Name} (id={Id}, index={Index})", dataRef.DataRefPath, id, index);
    }

    // ========================================================================
    // IXPlaneWebConnector: Set dataRef value (WebSocket or HTTP PATCH)
    // ========================================================================

    public async Task SetDataRefValueAsync(SimDataRef dataRef, float value)
    {
        await SetDataRefValueAsync(dataRef.DataRefPath, value);
    }

    public async Task SetDataRefValueAsync(SimStringDataRef dataRef, string value)
    {
        await SetDataRefValueAsync(dataRef.DataRefPath, value);
    }

    public async Task SetDataRefValueAsync(string dataRefPath, float value)
    {
        var (basePath, index) = ParseDataRefPath(dataRefPath);
        var id = await ResolveDataRefIdAsync(basePath);
        using var doc = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var jsonValue = doc.RootElement.Clone();
        var idx = index >= 0 ? index : (int?)null;

        switch (_transport)
        {
            case CommandSetDataRefTransport.Http:
                await SetDataRefValueByIdAsync(id, jsonValue, idx);
                break;

            case CommandSetDataRefTransport.WebSocket:
            default:
                await SetDataRefValuesByWsAsync([new DataRefSetEntry { Id = id, Value = jsonValue, Index = idx }]);
                break;
        }
        _logger.LogDebug("SetDataRefValueAsync Dataref: {dataRef}, Value:{value}", dataRefPath, value);
    }

    public async Task SetDataRefValueAsync(string dataRefPath, string value)
    {
        var (basePath, index) = ParseDataRefPath(dataRefPath);
        var id = await ResolveDataRefIdAsync(basePath);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        using var doc = JsonDocument.Parse($"\"{base64}\"");
        var jsonValue = doc.RootElement.Clone();
        var idx = index >= 0 ? index : (int?)null;

        switch (_transport)
        {
            case CommandSetDataRefTransport.Http:
                await SetDataRefValueByIdAsync(id, jsonValue, idx);
                break;

            case CommandSetDataRefTransport.WebSocket:
            default:
                await SetDataRefValuesByWsAsync([new DataRefSetEntry { Id = id, Value = jsonValue, Index = idx }]);
                break;
        }
        _logger.LogDebug("SetDataRefValueAsync Dataref: {dataRef}, Value:{value}", dataRefPath, value);
    }



    // ========================================================================
    // IXPlaneWebConnector: Send command (WebSocket for precise begin/end control)
    // ========================================================================

    public Task SendCommandAsync(SimCommand command) => SendCommandAsync(command, duration: 0);

    public async Task SendCommandAsync(SimCommand command, float duration)
    {
        if (duration is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be between 0 and 10 seconds.");

        var id = await ResolveCommandIdAsync(command.Command);

        switch (_transport)
        {
            case CommandSetDataRefTransport.Http:
                await ActivateCommandAsync(id, duration);
                break;

            case CommandSetDataRefTransport.WebSocket:
            default:
                await SetCommandActiveAsync(id, true, duration);
                break;
        }

        _logger.LogDebug("Sent command {Name} (id={Id}, transport={Transport}, duration={Duration}s)",
            command.Command, id, _transport, duration);
    }

    // ========================================================================
    // REST: Resolve dataRef/command names to session IDs
    // ========================================================================

    private async Task<long> ResolveDataRefIdAsync(string dataRefPath)
    {
        if (_dataRefIdCache.TryGetValue(dataRefPath, out var cachedId))
            return cachedId;

        var url = $"{_baseUrl}/datarefs?filter[name]={Uri.EscapeDataString(dataRefPath)}&fields=id,name";
        using var response = await _httpClientFactory.CreateClient().GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo);

        if (result?.Data is not { Count: > 0 })
            throw new KeyNotFoundException($"Dataref '{dataRefPath}' not found in X-Plane");

        var id = result.Data[0].Id;
        _dataRefIdCache[dataRefPath] = id;
        _reverseDataRefIdCache[id] = dataRefPath;
        _logger.LogDebug("Resolved dataRef {Name} ? id {Id}", dataRefPath, id);
        return id;
    }

    private async Task<long> ResolveCommandIdAsync(string commandPath)
    {
        if (_commandIdCache.TryGetValue(commandPath, out var cachedId))
            return cachedId;

        var url = $"{_baseUrl}/commands?filter[name]={Uri.EscapeDataString(commandPath)}&fields=id,name";
        using var response = await _httpClientFactory.CreateClient().GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneListResponseXPlaneCommandInfo);

        if (result?.Data is not { Count: > 0 })
            throw new KeyNotFoundException($"Command '{commandPath}' not found in X-Plane");

        var id = result.Data[0].Id;
        _commandIdCache[commandPath] = id;
        _reverseCommandIdCache[id] = commandPath;
        _logger.LogDebug("Resolved command {Name} ? id {Id}", commandPath, id);
        return id;
    }

    // ========================================================================
    // IXPlaneApi: REST — Capabilities
    // ========================================================================

    public async Task<XPlaneCapabilitiesResponse?> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var response = await _httpClientFactory.CreateClient().GetAsync(_capabilitiesUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneCapabilitiesResponse, ct);
    }

    // ========================================================================
    // IXPlaneApi: REST — Datarefs
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
        var result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo, ct);
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

    public async Task SetDataRefValueByIdAsync(long id, JsonElement value, int? index = null, CancellationToken ct = default)
    {
        var body = new DataRefValueBody { Data = value };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Models.XPlaneJsonContext.Default.DataRefValueBody);
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

        Task<HttpResponseMessage> SendAsync() => _httpClientFactory.CreateClient().PatchAsync($"{_baseUrl}/datarefs/{id}/value{indexParam}", content, ct);
    }

    // ========================================================================
    // IXPlaneApi: WebSocket — Dataref set values
    // ========================================================================

    public async Task SetDataRefValuesByWsAsync(IEnumerable<DataRefSetEntry> datarefs)
    {
        var request = new WsRequest<DataRefSetValuesParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_set_values",
            Params = new DataRefSetValuesParams { Datarefs = [.. datarefs] }
        };
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestDataRefSetValuesParams);
    }

    // ========================================================================
    // IXPlaneApi: WebSocket — Dataref unsubscribe
    // ========================================================================

    public async Task UnsubscribeDataRefsAsync(IEnumerable<DataRefSubscribeEntry> datarefs)
    {
        var request = new WsRequest<DataRefSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_unsubscribe_values",
            Params = new DataRefSubscribeParams { Datarefs = [.. datarefs] }
        };
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestDataRefSubscribeParams);
    }

    // ========================================================================
    // IXPlaneApi: REST — Commands
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

    public async Task ActivateCommandAsync(long id, float duration = 0, CancellationToken ct = default)
    {
        var body = new CommandActivateBody { Duration = duration };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Models.XPlaneJsonContext.Default.CommandActivateBody);
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

        Task<HttpResponseMessage> SendAsync() => _httpClientFactory.CreateClient().PostAsync($"{_baseUrl}/command/{id}/activate", content);
    }

    // ========================================================================
    // IXPlaneApi: REST — Flight
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
    // IXPlaneApi: WebSocket — Command subscriptions
    // ========================================================================

    public async Task SubscribeCommandUpdatesAsync(IEnumerable<long> commandIds, Action<long, bool> onUpdate)
    {
        var entries = new List<CommandIdEntry>();
        foreach (var id in commandIds)
        {
            _commandSubscriptions.AddOrUpdate(
                id,
                _ => ImmutableArray.Create(onUpdate),
                (_, existing) => existing.Add(onUpdate));
            entries.Add(new CommandIdEntry { Id = id });
        }

        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_subscribe_is_active",
            Params = new CommandSubscribeParams { Commands = entries }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    public async Task UnsubscribeCommandUpdatesAsync(IEnumerable<long> commandIds)
    {
        var entries = new List<CommandIdEntry>();
        foreach (var id in commandIds)
        {
            _commandSubscriptions.TryRemove(id, out _);
            entries.Add(new CommandIdEntry { Id = id });
        }

        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_unsubscribe_is_active",
            Params = new CommandSubscribeParams { Commands = entries }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    // ========================================================================
    // IXPlaneApi: WebSocket — Command activation
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
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestCommandSetActiveParams);
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

    // ========================================================================
    // IDisposable
    // ========================================================================

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();

        var ws = Interlocked.Exchange(ref _webSocket, null);
        ws?.Dispose();


    }
}
