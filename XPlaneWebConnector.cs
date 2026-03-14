using ChannelWorker;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Interfaces;
using nocscienceat.XPlaneWebConnector.Models;
using System.Collections.Concurrent;
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
    private readonly string _httpClientName;
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

    // Dataref session-ID caches (path ? XPlaneDataRefInfo which includes id). Cleared when X-Plane restarts.
    private readonly ConcurrentDictionary<string, XPlaneDataRefInfo> _dataRefIdCache = new();
    private readonly ConcurrentDictionary<long, string> _reverseDataRefIdCache = new();
    private readonly ConcurrentDictionary<string, long> _commandIdCache = new();
    private readonly ConcurrentDictionary<long, string> _reverseCommandIdCache = new();

    // dataRef subscriptions: (sessionId, arrayIndex) -> callbacks
    // Accessed exclusively on the Worker thread.
    private readonly Dictionary<(long Id, int Index), SubscriptionCallbacks> _subscriptions = new();

 // Tracks which array indices are subscribed per dataRef ID.
    // X-Plane sends array updates with values for subscribed indices in sorted order;
    // we need this map to correlate array positions back to the original indices.
    // Accessed exclusively on the Worker thread.
    private readonly Dictionary<long, SortedSet<int>> _subscribedIndices = new();

    // Per-consumer subscription registry: each SubscribeAsync call gets a unique GUID.
    // Multiple GUIDs can map to the same (Id, Index) when multiple panels subscribe
    // to the same dataRef. Used for per-consumer unsubscription with reference counting.
    // Accessed exclusively on the Worker thread.
    private readonly Dictionary<Guid, SubscriptionEntry> _subscriptionRegistry = new();

    // Command activation subscriptions: command session ID -> (element, callbacks)
    // Accessed exclusively on the Worker thread.
    private readonly Dictionary<long, (string commandPath, List<Action<bool>> Callbacks)> _commandSubscriptions = new();

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

    private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    // Readiness probe configuration
    private readonly string? _readinessProbeDataRef;
    private readonly int _readinessProbeMaxRetries;

    /// <inheritdoc />
    public event Action? ConnectionClosed;

    private static readonly HashSet<string> SupportedApiVersions = ["v1", "v2", "v3"];

    public XPlaneWebConnector(string host, int port, CommandSetDataRefTransport commandTransport, bool fireForgetOnHttpTransport, ILogger<XPlaneWebConnector> logger, IHttpClientFactory httpClientFactory, 
        string? readinessProbeDataRef = null, int readinessProbeMaxRetries = 0, string apiVersion = "v2", string httpClientName = "")
    {
        if (!SupportedApiVersions.Contains(apiVersion))
            throw new ArgumentException($"Unsupported API version '{apiVersion}'. Supported: {string.Join(", ", SupportedApiVersions)}", nameof(apiVersion));

        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
        _baseUrl = $"http://{host}:{port}/api/{apiVersion}";
        _wsUrl = $"ws://{host}:{port}/api/{apiVersion}";
        _capabilitiesUrl = $"http://{host}:{port}/api/capabilities";
        _readinessProbeDataRef = readinessProbeDataRef;
        _readinessProbeMaxRetries = readinessProbeMaxRetries;
        _transport = commandTransport;
        _fireForgetOnHttpTransport = fireForgetOnHttpTransport;
    }

    private HttpClient CreateHttpClient() => _httpClientFactory.CreateClient(_httpClientName);

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
               settings.ReadinessProbeDataRef, settings.ReadinessProbeMaxRetries, settings.ApiVersion, settings.HttpClientName)
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
        _subscribedIndices.Clear();
        _commandSubscriptions.Clear();
        _subscriptionRegistry.Clear();
        _logger.LogInformation("XPlaneWebConnector stopped");
    }

    // ========================================================================
    // IXPlaneWebConnector: Subscribe to dataRef updates (WebSocket)
    // ========================================================================

    private async Task<IDisposable> SubscribeAsyncInternal(string dataRefPath, XPlaneDataRefInfo xPlaneDataRefInfo, int index, Delegate onchange)
    {
        var subId = Guid.NewGuid();

        await _commandChannel.Writer.SendCommandAsync(new WorkerCommand.SubscribeDataRef(subId, xPlaneDataRefInfo, index, onchange));

        _logger.LogDebug("Subscribed to dataRef {Name} (X-Plane Id={Id}, Subscription Id={SubId}, Type={type})", dataRefPath, xPlaneDataRefInfo.Id, subId, xPlaneDataRefInfo.ValueType);
        return new SubscriptionHandle(subId, _commandChannel.Writer, dataRefPath);
    }

    public async Task<IDisposable> SubscribeAsync(string dataRefPath, Action<float> onchange)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeFloat, "float", supportsArrayIndex: true);
        return await SubscribeAsyncInternal(dataRefPath, info, index, onchange);
    }

    public async Task<IDisposable> SubscribeAsync(string dataRefPath, Action<int> onchange)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeInt, "int", supportsArrayIndex: true);
        return await SubscribeAsyncInternal(dataRefPath, info, index, onchange);
    }

    public async Task<IDisposable> SubscribeAsync(string dataRefPath, Action<double> onchange)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeDouble, "double", supportsArrayIndex: false);
        return await SubscribeAsyncInternal(dataRefPath, info, index, onchange);
    }

    public async Task<IDisposable> SubscribeAsync(string dataRefPath, Action<string> onchange)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeData, "data/string", supportsArrayIndex: false);
        return await SubscribeAsyncInternal(dataRefPath, info, index, onchange);
    }

    // ========================================================================
    // IXPlaneWebConnector: Set dataRef value (WebSocket or HTTP PATCH)
    // ========================================================================

    public async Task SetDataRefValueAsync(string dataRefPath, float value)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeFloat, "float", supportsArrayIndex: true);
        await SetDataRefValueCoreAsync(info, index, JsonSerializer.SerializeToElement(value));
    }

    public async Task SetDataRefValueAsync(string dataRefPath, int value)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeInt, "int", supportsArrayIndex: true);
        await SetDataRefValueCoreAsync(info, index, JsonSerializer.SerializeToElement(value));
    }

    public async Task SetDataRefValueAsync(string dataRefPath, double value)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeDouble, "double", supportsArrayIndex: false);
        await SetDataRefValueCoreAsync(info, index, JsonSerializer.SerializeToElement(value));
    }

    public async Task SetDataRefValueAsync(string dataRefPath, string value)
    {
        var (info, index) = await ResolveAndValidateDataRefAsync(dataRefPath, static i => i.IsTypeData, "data/string", supportsArrayIndex: false);
        await SetDataRefValueCoreAsync(info, index, JsonSerializer.SerializeToElement(Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));
    }

    private async Task SetDataRefValueCoreAsync(XPlaneDataRefInfo xPlaneDataRefInfo, int index, JsonElement jsonValue)
    {
        var idx = index >= 0 ? index : (int?)null;
        switch (_transport)
        {
            case CommandSetDataRefTransport.Http:
                await SetDataRefValueByHttpAsync(xPlaneDataRefInfo.Id, jsonValue, idx);
                break;

            case CommandSetDataRefTransport.WebSocket:
            default:
                await SetDataRefValuesByWsAsync([new DataRefSetEntry { Id = xPlaneDataRefInfo.Id, Value = jsonValue, Index = idx }]);
                break;
        }
        _logger.LogDebug("SetDataRefValueAsync Dataref: {dataRef}, Value:{value}", xPlaneDataRefInfo.Name, jsonValue);
    }

    // ========================================================================
    // Shared dataRef resolution + validation
    // ========================================================================

    private async Task<(XPlaneDataRefInfo Info, int Index)> ResolveAndValidateDataRefAsync(string dataRefPath, Func<XPlaneDataRefInfo, bool> typeCheck, string expectedTypeName, bool supportsArrayIndex)
    {
        var (basePath, index) = ParseDataRefPath(dataRefPath);

        if (!supportsArrayIndex && index >= 0)
            throw new InvalidOperationException($"DataRefs of type {expectedTypeName} do not support array indices, but '{dataRefPath}' includes index {index}.");

        XPlaneDataRefInfo info = await ResolveDataRefIdAsync(basePath);

        if (!typeCheck(info))
            throw new InvalidOperationException($"DataRef '{info.Name}' is of type '{info.ValueType}', but type {expectedTypeName} was requested.");

        if (supportsArrayIndex)
        {
            if (index >= 0 && !info.IsArrayType)
                throw new InvalidOperationException($"DataRef '{info.Name}' is not an array type, but '{dataRefPath}' includes index {index}.");
            if (index == -1 && info.IsArrayType)
                throw new InvalidOperationException($"DataRef '{info.Name}' is an array type, but '{dataRefPath}' does not include an index.");
        }

        _logger.LogDebug("XPlaneDataRefInfo: Id: {id}, Name: {name}, ValueType {type}, IsArrayType: {at}", info.Id, info.Name, info.ValueType, info.IsArrayType);

        return (info, index);
    }

    // ========================================================================
    // IXPlaneWebConnector: Send command (WebSocket for precise begin/end control)
    // ========================================================================

    public Task SendCommandAsync(string commandPath) => SendCommandAsync(commandPath, duration: 0);

    public async Task SendCommandAsync(string commandPath, float duration)
    {
        if (duration is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be between 0 and 10 seconds.");

        var id = await ResolveCommandIdAsync(commandPath);

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
            commandPath, id, _transport, duration);
    }

    // ========================================================================
    // REST: Resolve dataRef/command names to session IDs
    // ========================================================================

    private async Task<XPlaneDataRefInfo> ResolveDataRefIdAsync(string dataRefPath)
    {
        if (_dataRefIdCache.TryGetValue(dataRefPath, out var cachedXPlaneDataRefInfo))
            return cachedXPlaneDataRefInfo;

        var url = $"{_baseUrl}/datarefs?filter[name]={Uri.EscapeDataString(dataRefPath)}";
        using var response = await CreateHttpClient().GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        XPlaneListResponse<XPlaneDataRefInfo>? result = await JsonSerializer.DeserializeAsync(stream, Models.XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo);

        if (result?.Data is not { Count: > 0 })
            throw new KeyNotFoundException($"Dataref '{dataRefPath}' not found in X-Plane");

        XPlaneDataRefInfo xPlaneDataRefInfo = result.Data[0];
        _dataRefIdCache[dataRefPath] = xPlaneDataRefInfo;
        _reverseDataRefIdCache[xPlaneDataRefInfo.Id] = dataRefPath;
        _logger.LogDebug("Resolved dataRef {Name} ? id {Id}", dataRefPath, xPlaneDataRefInfo.Id);
        return xPlaneDataRefInfo;
    }

    private async Task<long> ResolveCommandIdAsync(string commandPath)
    {
        if (_commandIdCache.TryGetValue(commandPath, out var cachedId))
            return cachedId;

        var url = $"{_baseUrl}/commands?filter[name]={Uri.EscapeDataString(commandPath)}&fields=id,name";
        using var response = await CreateHttpClient().GetAsync(url);
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
    // IXPlaneWebConnector: Subscribe to command activation updates (WebSocket)
    // ========================================================================

    public async Task<IDisposable> SubscribeCommandAsync(string commandPath, Action<bool> onUpdate)
    {
        var id = await ResolveCommandIdAsync(commandPath);
        var subId = Guid.NewGuid();

        await _commandChannel.Writer.SendCommandAsync(
            new WorkerCommand.SubscribeCommand(subId, id, commandPath, onUpdate));

        _logger.LogDebug("Subscribed to command {Name} (id={Id}, subId={SubId})", commandPath, id, subId);
        return new SubscriptionHandle(subId, _commandChannel.Writer, commandPath);
    }

    // ========================================================================
    // IXPlaneWebConnector: Cache warm-up
    // ========================================================================

    public async Task PreResolveDataRefAsync(string dataRefPath)
    {
        try
        {
            var (basePath, _) = ParseDataRefPath(dataRefPath);
            await ResolveDataRefIdAsync(basePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-resolve dataref '{DataRefPath}' — will retry on first use", dataRefPath);
        }
    }

    public async Task PreResolveCommandAsync(string commandPath)
    {
        try
        {
            await ResolveCommandIdAsync(commandPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-resolve command '{CommandPath}' — will retry on first use", commandPath);
        }
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
