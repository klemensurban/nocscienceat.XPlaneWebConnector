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
public sealed partial class XPlaneWebConnector : IXPlaneWebConnector, IXPlaneApi
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

    // Per-consumer subscription registry: each SubscribeAsync call gets a unique GUID.
    // Multiple GUIDs can map to the same (Id, Index) when multiple panels subscribe
    // to the same dataRef. Used for future per-consumer unsubscription with reference counting.
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _subscriptionRegistry = new();

    // Command activation subscriptions: command session ID ? (element, callbacks)
    private readonly ConcurrentDictionary<long, (SimCommand Element, ImmutableArray<Action<SimCommand, bool>> Callbacks)> _commandSubscriptions = new();

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

    private static readonly HashSet<string> SupportedApiVersions = ["v1", "v2", "v3"];

    public XPlaneWebConnector(string host, int port, CommandSetDataRefTransport commandTransport, bool fireForgetOnHttpTransport, ILogger<XPlaneWebConnector> logger, IHttpClientFactory httpClientFactory, 
        string? readinessProbeDataRef = null, int readinessProbeMaxRetries = 0, string apiVersion = "v2")
    {
        if (!SupportedApiVersions.Contains(apiVersion))
            throw new ArgumentException($"Unsupported API version '{apiVersion}'. Supported: {string.Join(", ", SupportedApiVersions)}", nameof(apiVersion));

        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _baseUrl = $"http://{host}:{port}/api/{apiVersion}";
        _wsUrl = $"ws://{host}:{port}/api/{apiVersion}";
        _capabilitiesUrl = $"http://{host}:{port}/api/capabilities";
        _readinessProbeDataRef = readinessProbeDataRef;
        _readinessProbeMaxRetries = readinessProbeMaxRetries;
        _transport = commandTransport;
        _fireForgetOnHttpTransport = fireForgetOnHttpTransport;
    }

    /// <summary>
    /// DI-friendly constructor. Resolves settings from <see cref="IXPlaneWebConnectorSettings"/>.
    /// <para>Register in DI with:</para>
    /// <code>
    /// services.AddSingleton&lt;IXPlaneWebConnectorSettings&gt;(mySettings);
    /// services.AddSingleton&lt;IXPlaneWebConnector, XPlaneWebConnector&gt;();
    /// </code>
    /// </summary>
    public XPlaneWebConnector(IXPlaneWebConnectorSettings settings, ILogger<XPlaneWebConnector> logger, IHttpClientFactory httpClientFactory)
        : this(settings.Host, settings.Port,
               Enum.TryParse<CommandSetDataRefTransport>(settings.Transport, ignoreCase: true, out var parsed) ? parsed : CommandSetDataRefTransport.WebSocket,
               settings.FireForgetOnHttpTransport, logger, httpClientFactory,
               settings.ReadinessProbeDataRef, settings.ReadinessProbeMaxRetries, settings.ApiVersion)
    {
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
        _commandSubscriptions.Clear();
        _subscriptionRegistry.Clear();
        _logger.LogInformation("XPlaneWebConnector stopped");
    }

    // ========================================================================
    // IXPlaneWebConnector: Subscribe to dataRef updates (WebSocket)
    // ========================================================================

    public async Task<IDisposable> SubscribeAsync(SimDataRef dataRef, Action<SimDataRef>? onchange = null)
    {
        var (basePath, index) = ParseDataRefPath(dataRef.DataRefPath);
        var id = await ResolveDataRefIdAsync(basePath);
        var cb = onchange ?? ((_) => { });
        var subId = Guid.NewGuid();

        await _commandChannel.Writer.SendCommandAsync(
            new WorkerCommand.SubscribeNumeric(subId, id, index, dataRef, cb));

        _logger.LogDebug("Subscribed to dataRef {Name} (id={Id}, index={Index}, subId={SubId})", dataRef.DataRefPath, id, index, subId);
        return new SubscriptionHandle(subId, _commandChannel.Writer);
    }

    public async Task<IDisposable> SubscribeAsync(SimStringDataRef dataRef, Action<SimStringDataRef>? onchange = null)
    {
        var (basePath, index) = ParseDataRefPath(dataRef.DataRefPath);
        var id = await ResolveDataRefIdAsync(basePath);
        var cb = onchange ?? ((_) => { });
        var subId = Guid.NewGuid();

        await _commandChannel.Writer.SendCommandAsync(
            new WorkerCommand.SubscribeString(subId, id, index, dataRef, cb));

        _logger.LogDebug("Subscribed to string dataRef {Name} (id={Id}, index={Index}, subId={SubId})", dataRef.DataRefPath, id, index, subId);
        return new SubscriptionHandle(subId, _commandChannel.Writer);
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
    // IXPlaneApi: Dataref writes (used by SetDataRefValueAsync)
    // ========================================================================

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
    // IXPlaneApi: Command activation (used by SendCommandAsync)
    // ========================================================================

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
    // IXPlaneWebConnector: Subscribe to command activation updates (WebSocket)
    // ========================================================================

    public async Task<IDisposable> SubscribeCommandAsync(SimCommand command, Action<SimCommand, bool> onUpdate)
    {
        var id = await ResolveCommandIdAsync(command.Command);
        var subId = Guid.NewGuid();

        await _commandChannel.Writer.SendCommandAsync(
            new WorkerCommand.SubscribeCommand(subId, id, command, onUpdate));

        _logger.LogDebug("Subscribed to command {Name} (id={Id}, subId={SubId})", command.Command, id, subId);
        return new SubscriptionHandle(subId, _commandChannel.Writer);
    }

    // ========================================================================
    // IXPlaneApi: WebSocket — Command subscriptions
    // ========================================================================

    public async Task SubscribeCommandUpdatesAsync(IEnumerable<long> commandIds)
    {
        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_subscribe_is_active",
            Params = new CommandSubscribeParams { Commands = [.. commandIds.Select(id => new CommandIdEntry { Id = id })] }
        };
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    public async Task UnsubscribeCommandUpdatesAsync(IEnumerable<long> commandIds)
    {
        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_unsubscribe_is_active",
            Params = new CommandSubscribeParams { Commands = [.. commandIds.Select(id => new CommandIdEntry { Id = id })] }
        };
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
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
