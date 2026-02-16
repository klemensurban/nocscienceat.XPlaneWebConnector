using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Interfaces;
using nocscienceat.XPlaneWebConnector.Models;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Dataref session-ID caches (path ? id). Cleared when X-Plane restarts.
    private readonly ConcurrentDictionary<string, long> _dataRefIdCache = new();
    private readonly ConcurrentDictionary<long, string> _reverseDataRefIdCache = new();
    private readonly ConcurrentDictionary<string, long> _commandIdCache = new();
    private readonly ConcurrentDictionary<long, string> _reverseCommandIdCache = new();

    // Numeric dataref subscriptions: (sessionId, arrayIndex) ? (element, callback)
    private readonly ConcurrentDictionary<(long Id, int Index), (SimDataRef Element, Action<SimDataRef, float> Callback)> _subscriptions = new();

    // String/data dataref subscriptions: (sessionId, arrayIndex) ? (element, callback)
    private readonly ConcurrentDictionary<(long Id, int Index), (SimStringDataRef Element, Action<SimStringDataRef, string> Callback)> _stringSubscriptions = new();

    // Tracks which array indices are subscribed per dataref ID.
    // X-Plane sends array updates with values for subscribed indices in sorted order;
    // we need this map to correlate array positions back to the original indices.
    private readonly ConcurrentDictionary<long, SortedSet<int>> _subscribedIndices = new();

    // Command activation subscriptions: command session ID ? callback
    private readonly ConcurrentDictionary<long, Action<long, bool>> _commandSubscriptions = new();

    // Monotonically increasing request ID for WebSocket messages
    private int _nextReqId;

    // Incoming WebSocket messages are queued here so the receive loop
    // is never blocked by slow callback processing (e.g. serial port writes).
    private readonly Channel<byte[]> _incomingMessages = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Readiness probe configuration
    private readonly string? _readinessProbeDataRef;
    private readonly int _readinessProbeMaxRetries;

    /// <inheritdoc />
    public event Action? ConnectionClosed;

    /// <summary>
    /// Regex to extract array index from dataref paths like "AirbusFBW/Foo[7]".
    /// </summary>
    [GeneratedRegex(@"^(.+)\[(\d+)\]$")]
    private static partial Regex ArrayIndexRegex();

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
            var caps = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneCapabilitiesResponse, cancellationToken);
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

        _logger.LogInformation("Waiting for plugin dataref '{DataRef}' to become available ...", _readinessProbeDataRef);

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
                        stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo, cancellationToken);

                    if (result?.Data is { Count: > 0 })
                    {
                        _logger.LogInformation("Plugin dataref '{DataRef}' is available (after {Attempts} attempt(s))",
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
                _logger.LogWarning("Plugin dataref '{DataRef}' not available after {Max} retries, continuing anyway",
                    _readinessProbeDataRef, _readinessProbeMaxRetries);
                return;
            }

            _logger.LogDebug("Plugin dataref '{DataRef}' not yet available (attempt {Attempt}{MaxInfo}), retrying in {Interval}s ...",
                _readinessProbeDataRef, attempt,
                _readinessProbeMaxRetries > 0 ? $"/{_readinessProbeMaxRetries}" : "",
                interval.TotalSeconds);
            await Task.Delay(interval, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // ========================================================================
    // Dataref path parsing: "AirbusFBW/Foo[7]" ? ("AirbusFBW/Foo", 7)
    // ========================================================================

    private static (string BasePath, int Index) ParseDataRefPath(string path)
    {
        var match = ArrayIndexRegex().Match(path);
        if (match.Success)
            return (match.Groups[1].Value, int.Parse(match.Groups[2].Value));
        return (path, -1);
    }

    // ========================================================================
    // Lifecycle
    // ========================================================================

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _receiveTask = ConnectWebSocketAndReceiveAsync(_cts.Token);
        _logger.LogInformation("XPlaneWebConnector started (REST: {RestUrl}, WS: {WsUrl})", _baseUrl, _wsUrl);
    }

    public async Task StopAsync(int timeout = 5000)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();

            if (_receiveTask is not null)
            {
                try { await _receiveTask.WaitAsync(TimeSpan.FromMilliseconds(timeout)); }
                catch (TimeoutException) { _logger.LogWarning("WebSocket receive loop did not stop within {Timeout}ms", timeout); }
                catch (OperationCanceledException) { /* expected */ }
            }
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
    // IXPlaneWebConnector: Subscribe to dataref updates (WebSocket)
    // ========================================================================

    public async Task SubscribeAsync(SimDataRef dataref, Action<SimDataRef, float>? onchange = null)
    {
        var (basePath, index) = ParseDataRefPath(dataref.DataRef);
        var id = await ResolveDataRefIdAsync(basePath);

        _subscriptions[(id, index)] = (dataref, onchange ?? ((_, _) => { }));
        await SendDataRefSubscribeAsync(id, index);
        _logger.LogDebug("Subscribed to dataref {Name} (id={Id}, index={Index})", dataref.DataRef, id, index);
    }

    public async Task SubscribeAsync(SimStringDataRef dataref, Action<SimStringDataRef, string>? onchange = null)
    {
        var (basePath, index) = ParseDataRefPath(dataref.DataRef);
        var id = await ResolveDataRefIdAsync(basePath);

        _stringSubscriptions[(id, index)] = (dataref, onchange ?? ((_, _) => { }));
        await SendDataRefSubscribeAsync(id, index);
        _logger.LogDebug("Subscribed to string dataref {Name} (id={Id}, index={Index})", dataref.DataRef, id, index);
    }

    /// <summary>Shared helper: tracks array index and delegates to <see cref="SubscribeDataRefsAsync"/>.</summary>
    private async Task SendDataRefSubscribeAsync(long id, int index)
    {
        if (index >= 0)
        {
            var indices = _subscribedIndices.GetOrAdd(id, _ => new SortedSet<int>());
            lock (indices) { indices.Add(index); }
        }

        await SubscribeDataRefsAsync([new DataRefSubscribeEntry { Id = id, Index = index >= 0 ? JsonSerializer.SerializeToElement(index) : null }]);
    }

    // ========================================================================
    // IXPlaneWebConnector: Set dataref value (WebSocket or HTTP PATCH)
    // ========================================================================

    public async Task SetDataRefValueAsync(SimDataRef dataref, float value)
    {
        await SetDataRefValueAsync(dataref.DataRef, value);
    }

    public async Task SetDataRefValueAsync(SimStringDataRef dataref, string value)
    {
        await SetDataRefValueAsync(dataref.DataRef, value);
    }

    public async Task SetDataRefValueAsync(string dataref, float value)
    {
        var (basePath, index) = ParseDataRefPath(dataref);
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
        _logger.LogInformation("SetDataRefValueAsync Dataref: {dataRef}, Value:{value}", dataref, value);
    }

    public async Task SetDataRefValueAsync(string dataref, string value)
    {
        var (basePath, index) = ParseDataRefPath(dataref);
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
        _logger.LogInformation("SetDataRefValueAsync Dataref: {dataRef}, Value:{value}", dataref, value);
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
    // WebSocket connection and receive loop
    // ========================================================================

    private async Task ConnectWebSocketAndReceiveAsync(CancellationToken ct)
    {
        // Process incoming messages on a dedicated background task so the
        // receive loop is never blocked by slow callback processing (serial writes).
        var processingTask = Task.Run(() => ProcessIncomingMessagesAsync(ct), ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(new Uri(_wsUrl), ct);
                    _logger.LogInformation("WebSocket connected to {Url}", _wsUrl);

                    await ReceiveLoopAsync(ct);

                    // If the server closed cleanly the ConnectionClosed event was
                    // already raised — stop the reconnect loop so the host can
                    // shut down gracefully instead of retrying with stale state.
                    if (_webSocket.State == WebSocketState.CloseReceived)
                    {
                        _logger.LogInformation("Server closed connection, stopping reconnect loop");
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("WebSocket connection lost ({Error}), retrying once in 3s...", ex.Message);
                    _logger.LogDebug(ex, "WebSocket connection lost details");
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }

                    try
                    {
                        _webSocket = new ClientWebSocket();
                        await _webSocket.ConnectAsync(new Uri(_wsUrl), ct);
                        _logger.LogInformation("WebSocket reconnected to {Url}", _wsUrl);
                        continue; // success — re-enter the loop to start ReceiveLoopAsync
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogInformation("WebSocket reconnect failed ({Error}), signalling connection closed", retryEx.Message);
                        _logger.LogDebug(retryEx, "WebSocket reconnect failure details");
                        ConnectionClosed?.Invoke();
                        break;
                    }
                }
            }
        }
        finally
        {
            _incomingMessages.Writer.TryComplete();
            await processingTask;
        }
    }


    /// <summary>
    /// Reads WebSocket frames as fast as possible and enqueues them for
    /// processing.  Never calls user callbacks — that happens on the
    /// processing task — so serial-port writes cannot stall the read.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _webSocket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                ConnectionClosed?.Invoke();
                return;
            }

            byte[] messageBytes;

            if (result.EndOfMessage)
            {
                // Fast path: single-frame message (most common).
                // One exact-size allocation, no MemoryStream, no ToArray() copy.
                messageBytes = GC.AllocateUninitializedArray<byte>(result.Count);
                buffer.AsSpan(0, result.Count).CopyTo(messageBytes);
            }
            else
            {
                // Slow path: multi-frame message — assemble with ArrayPool
                messageBytes = await AssembleMultiFrameMessageAsync(buffer, result.Count, ct);
            }

            _incomingMessages.Writer.TryWrite(messageBytes);
        }
    }

    /// <summary>
    /// Assembles a multi-frame WebSocket message using pooled buffers
    /// for intermediate storage. Returns an exact-size byte[].
    /// </summary>
    private async Task<byte[]> AssembleMultiFrameMessageAsync(byte[] buffer, int firstFrameCount, CancellationToken ct)
    {
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var assembled = pool.Rent(buffer.Length * 2);
        int totalLength = 0;

        try
        {
            // Copy first frame data
            buffer.AsSpan(0, firstFrameCount).CopyTo(assembled);
            totalLength = firstFrameCount;

            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket!.ReceiveAsync(buffer, ct);

                // Grow pooled buffer if needed
                if (totalLength + result.Count > assembled.Length)
                {
                    var larger = pool.Rent((totalLength + result.Count) * 2);
                    assembled.AsSpan(0, totalLength).CopyTo(larger);
                    pool.Return(assembled);
                    assembled = larger;
                }

                buffer.AsSpan(0, result.Count).CopyTo(assembled.AsSpan(totalLength));
                totalLength += result.Count;
            }
            while (!result.EndOfMessage);

            // Exact-size copy for the channel (pooled buffer is returned below)
            var final = GC.AllocateUninitializedArray<byte>(totalLength);
            assembled.AsSpan(0, totalLength).CopyTo(final);
            return final;
        }
        finally
        {
            pool.Return(assembled);
        }
    }

    /// <summary>
    /// Background task that drains the incoming message queue and dispatches
    /// callbacks.  Runs on its own thread so slow serial-port writes do not
    /// block WebSocket reads.
    /// </summary>
    private async Task ProcessIncomingMessagesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var messageBytes in _incomingMessages.Reader.ReadAllAsync(ct))
            {
                try
                {
                    ProcessIncomingMessage(messageBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing incoming WebSocket message");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // ========================================================================
    // Incoming WebSocket message processing
    // ========================================================================

    private void ProcessIncomingMessage(byte[] messageBytes)
    {
        using var doc = JsonDocument.Parse(messageBytes);
        
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            return;

        switch (typeProp.GetString())
        {
            case "dataref_update_values":
                HandleDataRefUpdates(root);
                break;

            case "command_update_is_active":
                HandleCommandUpdates(root);
                break;

            case "result":
                var result = JsonSerializer.Deserialize(messageBytes, XPlaneJsonContext.Default.WsResultMessage);
                if (result is not null && !result.Success)
                    _logger.LogWarning("WebSocket request {ReqId} failed: [{Code}] {Message}",
                        result.ReqId, result.ErrorCode, result.ErrorMessage);
                break;

            default:
                _logger.LogInformation("Unhandled WebSocket message type: {Type}", typeProp.GetString());
                break;
        }
    }

    /// <summary>
    /// Dispatches dataref_update_values to registered callbacks.
    /// The "data" object has dynamic keys (dataref session IDs as strings)
    /// with values that are either scalars or arrays — parsed with JsonDocument.
    /// </summary>
    private void HandleDataRefUpdates(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in data.EnumerateObject())
        {
            if (!long.TryParse(prop.Name, out var id))
                continue;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    DispatchScalarUpdate(id, (float)prop.Value.GetDouble());
                    break;

                case JsonValueKind.Array:
                    DispatchArrayUpdate(id, prop.Value);
                    break;

                case JsonValueKind.String:
                    DispatchStringUpdate(id, prop.Value.GetString() ?? "");
                    break;
            }
        }
    }

    private void DispatchScalarUpdate(long id, float value)
    {
        if (_subscriptions.TryGetValue((id, -1), out var sub))
        {
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))            {
                _logger.LogDebug("Received scalar update for dataref {Name} (id={Id}): {Value}", name, id, value);
            }
            sub.Element.Value = value;
            try { sub.Callback(sub.Element, value); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error in dataref callback for id {Id}", id); }
        }
    }

    private void DispatchArrayUpdate(long id, JsonElement arrayElement)
    {
        // Check for string subscriptions first (data-type datarefs sent as arrays)
        if (_stringSubscriptions.TryGetValue((id, -1), out var strSub))
        {
            var decoded = DecodeByteArrayToString(arrayElement);
            strSub.Element.Value = decoded;
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))
            {
                _logger.LogDebug("Received array update(string) for dataref {Name} (id={Id}): {Value}", name, id, decoded);
            }

            try { strSub.Callback(strSub.Element, decoded); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error in string dataref callback for id {Id}", id); }
            return;
        }

        // Numeric array: map positional values back to subscribed indices
        if (_subscribedIndices.TryGetValue(id, out var indices))
        {
            int[] sortedIndices;
            lock (indices) { sortedIndices = [.. indices]; }

            int pos = 0;
            foreach (var element in arrayElement.EnumerateArray())
            {
                if (pos >= sortedIndices.Length) break;
                var idx = sortedIndices[pos];
                var val = (float)element.GetDouble();

                if (_subscriptions.TryGetValue((id, idx), out var sub))
                {
                    if (_reverseDataRefIdCache.TryGetValue(id, out string name))
                    {
                        if (name != "AirbusFBW/BatVolts")
                            _logger.LogDebug("Received array update(indexed) for dataref {Name} (id={Id}), Index {idx} : {Value}", name, id, idx, val );
                    }
                    sub.Element.Value = val;
                    try { sub.Callback(sub.Element, val); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error in dataref callback for id {Id}[{Index}]", id, idx); }
                }
                pos++;
            }
        }
        else
        {
            // No specific indices subscribed — treat whole array as full subscription
            int pos = 0;
            foreach (var element in arrayElement.EnumerateArray())
            {
                if (_subscriptions.TryGetValue((id, pos), out var sub))
                {
                    var val = (float)element.GetDouble();
                    sub.Element.Value = val;
                    if (_reverseDataRefIdCache.TryGetValue(id, out string name))
                    {
                        _logger.LogDebug("Received array update(full) for dataref {Name} (id={Id})  : {Value}", name, id, val);
                    }
                    try { sub.Callback(sub.Element, val); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error in dataref callback for id {Id}[{Index}]", id, pos); }
                }
                pos++;
            }
        }
    }

    private void DispatchStringUpdate(long id, string base64)
    {
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch { decoded = base64; }

        if (_stringSubscriptions.TryGetValue((id, -1), out var sub))
        {
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))
            {
                _logger.LogDebug("Received string for dataref {Name} (id={Id}): {Value}", name, id, decoded);
            }
            sub.Element.Value = decoded;
            try { sub.Callback(sub.Element, decoded); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error in string dataref callback for id {Id}", id); }
        }
    }

    private static string DecodeByteArrayToString(JsonElement arrayElement)
    {
        // data-type arrays: each element is a byte ? reassemble and decode
        var bytes = new byte[arrayElement.GetArrayLength()];
        int i = 0;
        foreach (var el in arrayElement.EnumerateArray())
        {
            bytes[i++] = (byte)el.GetInt32();
        }
        // Trim trailing nulls
        int len = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, len >= 0 ? len : bytes.Length);
    }

    /// <summary>
    /// Dispatches command_update_is_active to registered callbacks.
    /// </summary>
    private void HandleCommandUpdates(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in data.EnumerateObject())
        {
            if (!long.TryParse(prop.Name, out var id))
                continue;

            if (!_commandSubscriptions.TryGetValue(id, out var callback))
                continue;

            var isActive = prop.Value.ValueKind == JsonValueKind.True;
            if (_reverseCommandIdCache.TryGetValue(id, out string path))
            {
                _logger.LogDebug("Received HandleCommand for Commandpath {Name} (id={Id}):  active={active}", path , id, isActive);
            }
            try { callback(id, isActive); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error in command callback for id {Id}", id); }
        }
    }

    // ========================================================================
    // WebSocket send helper
    // ========================================================================

    /// <summary>
    /// Sends a fire-and-forget WebSocket message.
    /// X-Plane does not reliably deliver "result" responses while the receive loop
    /// is busy dispatching subscription callbacks (serial port writes, etc.),
    /// so we never block waiting for acknowledgements.
    /// </summary>
    private async Task SendWebSocketFireAndForgetAsync<T>(T request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, typeInfo);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
    }

    // ========================================================================
    // REST: Resolve dataref/command names to session IDs
    // ========================================================================

    private async Task<long> ResolveDataRefIdAsync(string dataRefPath)
    {
        if (_dataRefIdCache.TryGetValue(dataRefPath, out var cachedId))
            return cachedId;

        var url = $"{_baseUrl}/datarefs?filter[name]={Uri.EscapeDataString(dataRefPath)}&fields=id,name";
        using var response = await _httpClientFactory.CreateClient().GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo);

        if (result?.Data is not { Count: > 0 })
            throw new KeyNotFoundException($"Dataref '{dataRefPath}' not found in X-Plane");

        var id = result.Data[0].Id;
        _dataRefIdCache[dataRefPath] = id;
        _reverseDataRefIdCache[id] = dataRefPath;
        _logger.LogDebug("Resolved dataref {Name} ? id {Id}", dataRefPath, id);
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
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneCommandInfo);

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
        return await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneCapabilitiesResponse, ct);
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
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneDataRefInfo, ct);
        return result?.Data ?? [];
    }

    public async Task<int> GetDataRefCountAsync(CancellationToken ct = default)
    {
        using var response = await _httpClientFactory.CreateClient().GetAsync($"{_baseUrl}/datarefs/count", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneScalarResponseInt32, ct);
        return result?.Data ?? 0;
    }

    public async Task<JsonElement> GetDataRefValueAsync(long id, int? index = null, CancellationToken ct = default)
    {
        var indexParam = index.HasValue ? $"?index={index.Value}" : "";
        using var response = await _httpClientFactory.CreateClient().GetAsync($"{_baseUrl}/datarefs/{id}/value{indexParam}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneValueResponse, ct);
        return result?.Data ?? default;
    }

    public async Task SetDataRefValueByIdAsync(long id, JsonElement value, int? index = null, CancellationToken ct = default)
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
                    _logger.LogWarning(ex, "Failed to set dataref value via HTTP for id {Id}{Index}", id, index.HasValue ? $"[index={index.Value}]" : "");
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
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestDataRefSetValuesParams);
    }

    // ========================================================================
    // IXPlaneApi: WebSocket — Dataref unsubscribe
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

    public async Task UnsubscribeAllDataRefsAsync()
    {
        var request = new WsRequest<DataRefUnsubscribeAllParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_unsubscribe_values",
            Params = new DataRefUnsubscribeAllParams()
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestDataRefUnsubscribeAllParams);
        _subscriptions.Clear();
        _stringSubscriptions.Clear();
        _subscribedIndices.Clear();
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
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneListResponseXPlaneCommandInfo, ct);
        return result?.Data ?? [];
    }

    public async Task<int> GetCommandCountAsync(CancellationToken ct = default)
    {
        using var response = await _httpClientFactory.CreateClient().GetAsync($"{_baseUrl}/commands/count", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, XPlaneJsonContext.Default.XPlaneScalarResponseInt32, ct);
        return result?.Data ?? 0;
    }

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

        Task<HttpResponseMessage> SendAsync() => _httpClientFactory.CreateClient().PostAsync($"{_baseUrl}/command/{id}/activate", content);
    }

    // ========================================================================
    // IXPlaneApi: REST — Flight
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

        Task<HttpResponseMessage> SendAsync() => _httpClientFactory.CreateClient().PostAsync($"{_baseUrl}/flight", content, ct);
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
            _commandSubscriptions[id] = onUpdate;
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

    public async Task UnsubscribeAllCommandUpdatesAsync()
    {
        var request = new WsRequest<CommandUnsubscribeAllParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_unsubscribe_is_active",
            Params = new CommandUnsubscribeAllParams()
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandUnsubscribeAllParams);
        _commandSubscriptions.Clear();
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
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSetActiveParams);
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
