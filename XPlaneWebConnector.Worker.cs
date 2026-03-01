using ChannelWorker;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Models;
using System.Text;
using System.Text.Json;

namespace nocscienceat.XPlaneWebConnector;

public sealed partial class XPlaneWebConnector
{
    /// <summary>
    /// Thin adapter that delegates <see cref="IWorkerHandler{TCommand,TResult,TData}"/>
    /// calls to <see cref="XPlaneWebConnector"/> methods. Nested class so it can
    /// access private members.
    /// </summary>
    private sealed class DataRefWorkerHandler(XPlaneWebConnector connector) : IWorkerHandler<WorkerCommand, bool, XPlaneDataMessage>
    {
        public void HandleData(XPlaneDataMessage data) => connector.ProcessIncomingMessage(data);

        public Task<bool> HandleCommandAsync(WorkerCommand command, CancellationToken ct)
            => connector.HandleWorkerCommandAsync(command, ct);
    }

    // ========================================================================
    // Incoming WebSocket message processing
    // ========================================================================

    private void ProcessIncomingMessage(XPlaneDataMessage dataMessage)
    {
        byte[] messageBytes = dataMessage.MessageBytes;
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
                WsResultMessage? result = JsonSerializer.Deserialize(messageBytes, XPlaneJsonContext.Default.WsResultMessage);
                if (result is not null && !result.Success)
                    _logger.LogWarning("WebSocket request {ReqId} failed: [{Code}] {Message}",
                        result.ReqId, result.ErrorCode, result.ErrorMessage);
                break;

            default:
                _logger.LogWarning("Unhandled WebSocket message type: {Type}", typeProp.GetString());
                break;
        }
    }

    /// <summary>
    /// Dispatches dataref_update_values to registered callbacks.
    /// The "data" object has dynamic keys (dataRef session IDs as strings)
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
                _logger.LogDebug("Received scalar update for dataRef {Name} (id={Id}): {Value}", name, id, value);
            }
            foreach (Action<float> cb in sub.Callbacks)
            {
                _callbacks.Writer.TryWrite(new CallbackItem.SimDataRefCb(cb, value));
            }
        }
    }

    private void DispatchArrayUpdate(long id, JsonElement arrayElement)
    {
        // Check for string subscriptions first (data-type datarefs sent as arrays)
        if (_stringSubscriptions.TryGetValue((id, -1), out var strSub))
        {
            string decoded = DecodeByteArrayToString(arrayElement);
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))
            {
                _logger.LogDebug("Received array update(string) for dataRef {Name} (id={Id}): {Value}", name, id, decoded);
            }

            foreach (var cb in strSub.Callbacks)
            {
                _callbacks.Writer.TryWrite(new CallbackItem.SimStringDataRefCb(cb, decoded));
            }
            return;
        }

        // Numeric array: map positional values back to subscribed indices
        if (_subscribedIndices.TryGetValue(id, out var indices))
        {
            int[] sortedIndices = [.. indices];

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
                        // if (name != "AirbusFBW/BatVolts")
                            _logger.LogDebug("Received array update(indexed) for dataRef {Name} (id={Id}), Index {idx} : {Value}", name, id, idx, val );
                    }
                    foreach (var cb in sub.Callbacks)
                    {
                        _callbacks.Writer.TryWrite(new CallbackItem.SimDataRefCb(cb, val));
                    }
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
                    if (_reverseDataRefIdCache.TryGetValue(id, out string name))
                    {
                        _logger.LogDebug("Received array update(full) for dataRef {Name} (id={Id})  : {Value}", name, id, val);
                    }
                    foreach (var cb in sub.Callbacks)
                    {
                        _callbacks.Writer.TryWrite(new CallbackItem.SimDataRefCb(cb, val));
                    }
                }
                pos++;
            }
        }
    }

    private void DispatchStringUpdate(long id, string base64)
    {
        string decoded;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            int len = Array.IndexOf(bytes, (byte)0);
            decoded = Encoding.UTF8.GetString(bytes, 0, len >= 0 ? len : bytes.Length);
        }
        catch { decoded = base64; }

        if (_stringSubscriptions.TryGetValue((id, -1), out var sub))
        {
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))
            {
                _logger.LogDebug("Received string for dataRef {Name} (id={Id}): {Value}", name, id, decoded);
            }
            foreach (var cb in sub.Callbacks)
            {
                _callbacks.Writer.TryWrite(new CallbackItem.SimStringDataRefCb(cb, decoded));
            }
        }
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

            if (!_commandSubscriptions.TryGetValue(id, out var sub))
                continue;

            var isActive = prop.Value.ValueKind == JsonValueKind.True;
            if (_reverseCommandIdCache.TryGetValue(id, out string path))
            {
                _logger.LogDebug("Received HandleCommand for Commandpath {Name} (id={Id}):  active={active}", path , id, isActive);
            }
            foreach (var cb in sub.Callbacks)
            {
                _callbacks.Writer.TryWrite(new CallbackItem.CommandCb(cb, isActive));
            }
        }
    }

    // ========================================================================
    // Worker: handler and subscription registration helpers
    // ========================================================================

    /// <summary>
    /// Registers a numeric dataRef subscription. Returns true if this is a new subscription.
    /// Called on the Worker thread via <see cref="HandleWorkerCommandAsync"/>.
    /// </summary>
    private bool RegisterNumericSubscription(Guid subscriptionId, long id, int index, DataRef.Float dataref, Action<float> callback)
    {
        bool added = false;
        var key = (id, index);
        if (_subscriptions.TryGetValue(key, out var existing))
        {
            existing.Callbacks.Add(callback);
        }
        else
        {
            _subscriptions[key] = (dataref, new List<Action<float>> { callback });
            added = true;
        }

        _subscriptionRegistry[subscriptionId] = new SubscriptionEntry(id, index, SubscriptionKind.Numeric, callback);

        return added;
    }

    /// <summary>
    /// Registers a string dataRef subscription. Returns true if this is a new subscription.
    /// Called on the Worker thread via <see cref="HandleWorkerCommandAsync"/>.
    /// </summary>
    private bool RegisterStringSubscription(Guid subscriptionId, long id, int index, DataRef.String element, Action<string> callback)
    {
        bool added = false;
        var key = (id, index);
        if (_stringSubscriptions.TryGetValue(key, out var existing))
        {
            existing.Callbacks.Add(callback);
        }
        else
        {
            _stringSubscriptions[key] = (element, new List<Action<string>> { callback });
            added = true;
        }

        _subscriptionRegistry[subscriptionId] = new SubscriptionEntry(id, index, SubscriptionKind.String, callback);

        return added;
    }

    /// <summary>
    /// Registers a command activation subscription. Returns true if this is a new subscription.
    /// Called on the Worker thread via <see cref="HandleWorkerCommandAsync"/>.
    /// </summary>
    private bool RegisterCommandSubscription(Guid subscriptionId, long id, SimCommand element, Action<bool> callback)
    {
        bool added = false;
        if (_commandSubscriptions.TryGetValue(id, out var existing))
        {
            existing.Callbacks.Add(callback);
        }
        else
        {
            _commandSubscriptions[id] = (element, new List<Action<bool>> { callback });
            added = true;
        }

        _subscriptionRegistry[subscriptionId] = new SubscriptionEntry(id, -1, SubscriptionKind.Command, callback);

        return added;
    }

    /// <summary>
    /// Processes Worker commands on the Worker's single thread.
    /// Handles subscription registration and sends WebSocket subscribe messages.
    /// </summary>
    private async Task<bool> HandleWorkerCommandAsync(WorkerCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case WorkerCommand.SubscribeNumeric sub:
                DataRef.Float dataref = new DataRef.Float(sub.dataRefPath);
                if (RegisterNumericSubscription(sub.SubscriptionId, sub.Id, sub.Index, dataref, sub.Callback))
                    await SendDataRefSubscribeAsync(sub.Id, sub.Index);
                return true;

            case WorkerCommand.SubscribeString sub:
                DataRef.String stringdataref = new DataRef.String(sub.dataRefPath);
                if (RegisterStringSubscription(sub.SubscriptionId, sub.Id, sub.Index, stringdataref, sub.Callback))
                    await SendDataRefSubscribeAsync(sub.Id, sub.Index);
                return true;

            case WorkerCommand.UnsubscribeByGuid unsub:
                await HandleUnsubscribeByGuidAsync(unsub.SubscriptionId);
                return true;

            case WorkerCommand.SubscribeCommand sub:
                if (RegisterCommandSubscription(sub.SubscriptionId, sub.Id, sub.Element, sub.Callback))
                    await SendCommandSubscribeAsync(sub.Id);
                return true;

            case WorkerCommand.UnsubscribeAllDataRefs:
                await UnsubscribeAllDataRefsAsync();
                return true;

            case WorkerCommand.UnsubscribeAllCommands:
                await UnsubscribeAllCommandUpdatesAsync();
                return true;

            default:
                _logger.LogWarning("Unknown worker command type: {Type}", command.GetType().Name);
                return false;
        }
    }

    // ========================================================================
    // Worker: WebSocket subscribe/unsubscribe helpers
    // Called exclusively from HandleWorkerCommandAsync on the worker thread.
    // ========================================================================

    /// <summary>
    /// Removes a single consumer's callback identified by GUID.
    /// If no consumers remain for that (id, index), unsubscribes from X-Plane
    /// and cleans up _subscribedIndices.
    /// </summary>
    private async Task HandleUnsubscribeByGuidAsync(Guid subscriptionId)
    {
        if (!_subscriptionRegistry.Remove(subscriptionId, out var entry))
        {
            _logger.LogDebug("Unsubscribe: GUID {SubId} not found in registry (already removed?)", subscriptionId);
            return;
        }

        var key = (entry.DataRefId, entry.Index);
        bool lastConsumer = false;

        switch (entry.Kind)
        {
            case SubscriptionKind.Numeric:
                if (_subscriptions.TryGetValue(key, out var numSub))
                {
                    numSub.Callbacks.Remove((Action<float>)entry.Callback);
                    if (numSub.Callbacks.Count == 0)
                    {
                        _subscriptions.Remove(key);
                        lastConsumer = true;
                    }
                }
                break;

            case SubscriptionKind.String:
                if (_stringSubscriptions.TryGetValue(key, out var strSub))
                {
                    strSub.Callbacks.Remove((Action<string>)entry.Callback);
                    if (strSub.Callbacks.Count == 0)
                    {
                        _stringSubscriptions.Remove(key);
                        lastConsumer = true;
                    }
                }
                break;

            case SubscriptionKind.Command:
                if (_commandSubscriptions.TryGetValue(entry.DataRefId, out var cmdSub))
                {
                    cmdSub.Callbacks.Remove((Action<bool>)entry.Callback);
                    if (cmdSub.Callbacks.Count == 0)
                    {
                        _commandSubscriptions.Remove(entry.DataRefId);
                        lastConsumer = true;
                    }
                }
                break;
        }

        if (lastConsumer)
        {
            if (entry.Kind is SubscriptionKind.Numeric or SubscriptionKind.String)
            {
                // Clean up index tracking
                if (entry.Index >= 0 && _subscribedIndices.TryGetValue(entry.DataRefId, out var indices))
                {
                    indices.Remove(entry.Index);
                    if (indices.Count == 0)
                        _subscribedIndices.Remove(entry.DataRefId);
                }

                // Tell X-Plane to stop sending updates for this dataRef
                await UnsubscribeDataRefsAsync([new DataRefSubscribeEntry
                {
                    Id = entry.DataRefId,
                    Index = entry.Index >= 0 ? JsonSerializer.SerializeToElement(entry.Index) : null
                }]);

                _logger.LogDebug("Unsubscribed from X-Plane: dataRef id={Id}, index={Index} (last consumer removed)",
                    entry.DataRefId, entry.Index);
            }
            else if (entry.Kind == SubscriptionKind.Command)
            {
                await SendCommandUnsubscribeAsync(entry.DataRefId);

                _logger.LogDebug("Unsubscribed from X-Plane: command id={Id} (last consumer removed)",
                    entry.DataRefId);
            }
        }
        else
        {
            _logger.LogDebug("Removed consumer {SubId} from id={Id}, index={Index}, kind={Kind} (other consumers remain)",
                subscriptionId, entry.DataRefId, entry.Index, entry.Kind);
        }
    }

    /// <summary>Shared helper: tracks array index and delegates to <see cref="SubscribeDataRefsAsync"/>.</summary>
    private async Task SendDataRefSubscribeAsync(long id, int index)
    {
        if (index >= 0)
        {
            if (!_subscribedIndices.TryGetValue(id, out var indices))
            {
                indices = new SortedSet<int>();
                _subscribedIndices[id] = indices;
            }
            indices.Add(index);
        }

        await SubscribeDataRefsAsync([new DataRefSubscribeEntry { Id = id, Index = index >= 0 ? JsonSerializer.SerializeToElement(index) : null }]);
    }

    /// <summary>Sends a command_subscribe_is_active WS message for a single command ID.</summary>
    private Task SendCommandSubscribeAsync(long id) => SubscribeCommandUpdatesAsync([id]);

    /// <summary>Sends a command_unsubscribe_is_active WS message for a single command ID.</summary>
    private Task SendCommandUnsubscribeAsync(long id) => UnsubscribeCommandUpdatesAsync([id]);

    public async Task UnsubscribeAllDataRefsAsync()
    {
        var request = new WsRequest<DataRefUnsubscribeAllParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_unsubscribe_values",
            Params = new DataRefUnsubscribeAllParams()
        };
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestDataRefUnsubscribeAllParams);
        _subscriptions.Clear();
        _stringSubscriptions.Clear();
        _subscribedIndices.Clear();
        var dataRefKeys = _subscriptionRegistry
            .Where(kvp => kvp.Value.Kind is SubscriptionKind.Numeric or SubscriptionKind.String)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in dataRefKeys)
            _subscriptionRegistry.Remove(key);
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
        var commandKeys = _subscriptionRegistry
            .Where(kvp => kvp.Value.Kind == SubscriptionKind.Command)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in commandKeys)
            _subscriptionRegistry.Remove(key);
    }
}