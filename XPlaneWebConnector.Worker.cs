using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Models;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ChannelWorker;

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
                HandleDataRefUpdates(root, dataMessage.TimeStamp);
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
    private void HandleDataRefUpdates(JsonElement root, long timeStamp)
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
                    DispatchScalarUpdate(id, (float)prop.Value.GetDouble(), timeStamp);
                    break;

                case JsonValueKind.Array:
                    DispatchArrayUpdate(id, prop.Value, timeStamp);
                    break;

                case JsonValueKind.String:
                    DispatchStringUpdate(id, prop.Value.GetString() ?? "", timeStamp);
                    break;
            }
        }
    }

    private void DispatchScalarUpdate(long id, float value, long timeStamp)
    {
        if (_subscriptions.TryGetValue((id, -1), out var sub))
        {
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))            {
                _logger.LogDebug("Received scalar update for dataRef {Name} (id={Id}): {Value}", name, id, value);
            }
            sub.Element.Value = value;
            sub.Element.TimeSTamp = timeStamp;
            foreach (Action<SimDataRef> cb in sub.Callbacks)
            {
                // try { cb(sub.Element); }
                // catch (Exception ex) { _logger.LogWarning(ex, "Error in dataRef callback for id {Id}", id); }
                _callbacks.Writer.TryWrite(new CallbackItem.SimDataRefCb(cb, sub.Element));
            }
        }
    }

    private void DispatchArrayUpdate(long id, JsonElement arrayElement, long timeStamp)
    {
        // Check for string subscriptions first (data-type datarefs sent as arrays)
        if (_stringSubscriptions.TryGetValue((id, -1), out var strSub))
        {
            string decoded = DecodeByteArrayToString(arrayElement);
            strSub.Element.Value = decoded;
            strSub.Element.TimeSTamp = timeStamp;
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))
            {
                _logger.LogDebug("Received array update(string) for dataRef {Name} (id={Id}): {Value}", name, id, decoded);
            }

            foreach (var cb in strSub.Callbacks)
            {
                //try { cb(strSub.Element); }
                //catch (Exception ex) { _logger.LogWarning(ex, "Error in string dataRef callback for id {Id}", id); }
                _callbacks.Writer.TryWrite(new CallbackItem.SimStringDataRefCb(cb, strSub.Element));
            }
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
                        // if (name != "AirbusFBW/BatVolts")
                            _logger.LogDebug("Received array update(indexed) for dataRef {Name} (id={Id}), Index {idx} : {Value}", name, id, idx, val );
                    }
                    sub.Element.Value = val;
                    sub.Element.TimeSTamp = timeStamp;
                    foreach (var cb in sub.Callbacks)
                    {
                        //try { cb(sub.Element); }
                        //catch (Exception ex) { _logger.LogWarning(ex, "Error in dataRef callback for id {Id}[{Index}]", id, idx); }
                        _callbacks.Writer.TryWrite(new CallbackItem.SimDataRefCb(cb, sub.Element));
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
                    sub.Element.Value = val;
                    sub.Element.TimeSTamp = timeStamp;
                    if (_reverseDataRefIdCache.TryGetValue(id, out string name))
                    {
                        _logger.LogDebug("Received array update(full) for dataRef {Name} (id={Id})  : {Value}", name, id, val);
                    }
                    foreach (var cb in sub.Callbacks)
                    {
                        //try { cb(sub.Element); }
                        //catch (Exception ex) { _logger.LogWarning(ex, "Error in dataRef callback for id {Id}[{Index}]", id, pos); }
                        _callbacks.Writer.TryWrite(new CallbackItem.SimDataRefCb(cb, sub.Element));
                    }
                }
                pos++;
            }
        }
    }

    private void DispatchStringUpdate(long id, string base64, long timeStamp)
    {
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch { decoded = base64; }

        if (_stringSubscriptions.TryGetValue((id, -1), out var sub))
        {
            if (_reverseDataRefIdCache.TryGetValue(id, out string name))
            {
                _logger.LogDebug("Received string for dataRef {Name} (id={Id}): {Value}", name, id, decoded);
            }
            sub.Element.Value = decoded;
            sub.Element.TimeSTamp = timeStamp;
            foreach (var cb in sub.Callbacks)
            {
                //try { cb(sub.Element); }
                //catch (Exception ex) { _logger.LogWarning(ex, "Error in string dataRef callback for id {Id}", id); }
                _callbacks.Writer.TryWrite(new CallbackItem.SimStringDataRefCb(cb, sub.Element));
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
                _callbacks.Writer.TryWrite(new CallbackItem.CommandCb(cb, sub.Element, isActive));
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
    private bool RegisterNumericSubscription(Guid subscriptionId, long id, int index, SimDataRef element, Action<SimDataRef> callback)
    {
        bool added = false;
        _subscriptions.AddOrUpdate(
            (id, index),
            _ => { added = true; return (element, ImmutableArray.Create(callback)); },
            (_, existing) => (existing.Element, existing.Callbacks.Add(callback)));

        _subscriptionRegistry[subscriptionId] = new SubscriptionEntry(id, index, SubscriptionKind.Numeric, callback);

        return added;
    }

    /// <summary>
    /// Registers a string dataRef subscription. Returns true if this is a new subscription.
    /// Called on the Worker thread via <see cref="HandleWorkerCommandAsync"/>.
    /// </summary>
    private bool RegisterStringSubscription(Guid subscriptionId, long id, int index, SimStringDataRef element, Action<SimStringDataRef> callback)
    {
        bool added = false;
        _stringSubscriptions.AddOrUpdate(
            (id, index),
            _ => { added = true; return (element, ImmutableArray.Create(callback)); },
            (_, existing) => (existing.Element, existing.Callbacks.Add(callback)));

        _subscriptionRegistry[subscriptionId] = new SubscriptionEntry(id, index, SubscriptionKind.String, callback);

        return added;
    }

    /// <summary>
    /// Registers a command activation subscription. Returns true if this is a new subscription.
    /// Called on the Worker thread via <see cref="HandleWorkerCommandAsync"/>.
    /// </summary>
    private bool RegisterCommandSubscription(Guid subscriptionId, long id, SimCommand element, Action<SimCommand, bool> callback)
    {
        bool added = false;
        _commandSubscriptions.AddOrUpdate(
            id,
            _ => { added = true; return (element, ImmutableArray.Create(callback)); },
            (_, existing) => (existing.Element, existing.Callbacks.Add(callback)));

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
                if (RegisterNumericSubscription(sub.SubscriptionId, sub.Id, sub.Index, sub.Element, sub.Callback))
                    await SendDataRefSubscribeAsync(sub.Id, sub.Index);
                return true;

            case WorkerCommand.SubscribeString sub:
                if (RegisterStringSubscription(sub.SubscriptionId, sub.Id, sub.Index, sub.Element, sub.Callback))
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
        if (!_subscriptionRegistry.TryRemove(subscriptionId, out var entry))
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
                    var updated = numSub.Callbacks.Remove((Action<SimDataRef>)entry.Callback);
                    if (updated.IsEmpty)
                    {
                        _subscriptions.TryRemove(key, out _);
                        lastConsumer = true;
                    }
                    else
                    {
                        _subscriptions[key] = (numSub.Element, updated);
                    }
                }
                break;

            case SubscriptionKind.String:
                if (_stringSubscriptions.TryGetValue(key, out var strSub))
                {
                    var updated = strSub.Callbacks.Remove((Action<SimStringDataRef>)entry.Callback);
                    if (updated.IsEmpty)
                    {
                        _stringSubscriptions.TryRemove(key, out _);
                        lastConsumer = true;
                    }
                    else
                    {
                        _stringSubscriptions[key] = (strSub.Element, updated);
                    }
                }
                break;

            case SubscriptionKind.Command:
                if (_commandSubscriptions.TryGetValue(entry.DataRefId, out var cmdSub))
                {
                    var updated = cmdSub.Callbacks.Remove((Action<SimCommand, bool>)entry.Callback);
                    if (updated.IsEmpty)
                    {
                        _commandSubscriptions.TryRemove(entry.DataRefId, out _);
                        lastConsumer = true;
                    }
                    else
                    {
                        _commandSubscriptions[entry.DataRefId] = (cmdSub.Element, updated);
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
                    lock (indices)
                    {
                        indices.Remove(entry.Index);
                        if (indices.Count == 0)
                            _subscribedIndices.TryRemove(entry.DataRefId, out _);
                    }
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
            var indices = _subscribedIndices.GetOrAdd(id, _ => new SortedSet<int>());
            lock (indices) { indices.Add(index); }
        }

        await SubscribeDataRefsAsync([new DataRefSubscribeEntry { Id = id, Index = index >= 0 ? JsonSerializer.SerializeToElement(index) : null }]);
    }

    /// <summary>Sends a command_subscribe_is_active WS message for a single command ID.</summary>
    private async Task SendCommandSubscribeAsync(long id)
    {
        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_subscribe_is_active",
            Params = new CommandSubscribeParams { Commands = [new CommandIdEntry { Id = id }] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    /// <summary>Sends a command_unsubscribe_is_active WS message for a single command ID.</summary>
    private async Task SendCommandUnsubscribeAsync(long id)
    {
        var request = new WsRequest<CommandSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "command_unsubscribe_is_active",
            Params = new CommandSubscribeParams { Commands = [new CommandIdEntry { Id = id }] }
        };
        await SendWebSocketFireAndForgetAsync(request, XPlaneJsonContext.Default.WsRequestCommandSubscribeParams);
    }

    public async Task SubscribeDataRefsAsync(IEnumerable<DataRefSubscribeEntry> datarefs)
    {
        var request = new WsRequest<DataRefSubscribeParams>
        {
            ReqId = Interlocked.Increment(ref _nextReqId),
            Type = "dataref_subscribe_values",
            Params = new DataRefSubscribeParams { Datarefs = [.. datarefs] }
        };
        await SendWebSocketFireAndForgetAsync(request, Models.XPlaneJsonContext.Default.WsRequestDataRefSubscribeParams);
    }

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
        foreach (var kvp in _subscriptionRegistry)
        {
            if (kvp.Value.Kind is SubscriptionKind.Numeric or SubscriptionKind.String)
                _subscriptionRegistry.TryRemove(kvp.Key, out _);
        }
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
        foreach (var kvp in _subscriptionRegistry)
        {
            if (kvp.Value.Kind == SubscriptionKind.Command)
                _subscriptionRegistry.TryRemove(kvp.Key, out _);
        }
    }
}