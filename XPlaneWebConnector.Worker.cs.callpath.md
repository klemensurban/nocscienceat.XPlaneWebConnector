# XPlaneWebConnector.Worker.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.Worker.cs`
> **Responsibility:** All code that runs exclusively on the Worker's single-threaded loop.
> **Thread context:** Dedicated worker thread with custom `SynchronizationContext`.
> **Thread safety:** All methods in this file are serialized — no locks needed for subscription state.

---

## Channel Dataflow — This File's Role

The Worker is the **hub** of the channel architecture. It is the sole reader of
`_dataChannel` (inbound from X-Plane) and `_commandChannel` (inbound from consumer),
and the sole writer to `_callbacks` (outbound to consumer).

```
  X-Plane                                                            Consumer
     │                                                                  │
     │  WS frames                                             SubscribeAsync()
     ▼                                                        StopAsync()
 ┌───────────────┐                                                      │
 │ Transport.cs  │                                                      │
 │ (receive loop)│                                                      │
 └──────┬────────┘                                                      │
        │                                                               │
        ▼                                                               ▼
 ┌──────────────┐     ┌─────────────────────────────────┐    ┌──────────────────┐
 │ _dataChannel │────►│        Worker.cs                │◄───│ _commandChannel  │
 │   (inbound)  │     │     (single-threaded loop)      │    │    (inbound)     │
 └──────────────┘     │                                 │    └──────────────────┘
                      │  reads both channels            │
                      │  processes data + commands      │
                      │  writes dispatch results:       │
                      └────────────┬────────────────────┘
                                   │
                                   ▼
                      ┌────────────────────┐
                      │  _callbacks        │
                      │  (outbound)        │
                      └─────────┬──────────┘
                                │
                                ▼
                      ┌─────────────────────┐
                      │ CallbackChannel.cs  │──► Consumer callbacks
                      │ (callback thread)   │   (panel updates, serial writes)
                      └─────────────────────┘
```

### Channel interactions in this file

| Channel | Direction | Method(s) |
|---|---|---|
| `_dataChannel` | **reads** ← | `DataRefWorkerHandler.HandleData` → `ProcessIncomingMessage` |
| `_commandChannel` | **reads** ← | `DataRefWorkerHandler.HandleCommandAsync` → `HandleWorkerCommandAsync` |
| `_callbacks` | **writes** → | `DispatchScalarUpdate`, `DispatchArrayUpdate`, `DispatchStringUpdate`, `HandleCommandUpdates` |

---

## Worker Entry Point

```
Worker<TCommand,TResult,TData>.RunAsync()           ── generic Worker (Worker.cs)
  └─ ExecuteLoopAsync()
       ├─ HandleData    ──► DataRefWorkerHandler.HandleData()
       │                      └─► ProcessIncomingMessage()          ── this file
       └─ HandleCommand ──► DataRefWorkerHandler.HandleCommandAsync()
                              └─► HandleWorkerCommandAsync()        ── this file
```

---

## Data Path: HandleData → ProcessIncomingMessage

```
ProcessIncomingMessage(XPlaneDataMessage)
  ├─ type = "dataref_update_values"
  │    └─ HandleDataRefUpdates(root, timeStamp)
  │         ├─ JsonValueKind.Number
  │         │    └─ DispatchScalarUpdate(id, value, timeStamp)
  │         │         └─ _callbacks.Writer.TryWrite(SimDataRefCb)   ──► CallbackChannel.cs
  │         ├─ JsonValueKind.Array
  │         │    └─ DispatchArrayUpdate(id, arrayElement, timeStamp)
  │         │         ├─ string sub?  DecodeByteArrayToString()     ──► Utility.cs
  │         │         │    └─ _callbacks.Writer.TryWrite(SimStringDataRefCb)
  │         │         ├─ indexed sub? _subscribedIndices lookup
  │         │         │    └─ _callbacks.Writer.TryWrite(SimDataRefCb)
  │         │         └─ full array?  positional mapping
  │         │              └─ _callbacks.Writer.TryWrite(SimDataRefCb)
  │         └─ JsonValueKind.String
  │              └─ DispatchStringUpdate(id, base64, timeStamp)
  │                   └─ _callbacks.Writer.TryWrite(SimStringDataRefCb)
  │
  ├─ type = "command_update_is_active"
  │    └─ HandleCommandUpdates(root)
  │         └─ _callbacks.Writer.TryWrite(CommandCb)                ──► CallbackChannel.cs
  │
  └─ type = "result"
       └─ log warning if failed
```

---

## Command Path: HandleCommand → HandleWorkerCommandAsync

```
HandleWorkerCommandAsync(WorkerCommand, ct)
  │
  ├─ SubscribeNumeric
  │    ├─ RegisterNumericSubscription(guid, id, index, element, callback)
  │    │    └─ _subscriptionRegistry[guid] = new SubscriptionEntry(...)
  │    └─ SendDataRefSubscribeAsync(id, index)
  │         └─ SubscribeDataRefsAsync(datarefs)
  │              └─ SendWebSocketFireAndForgetAsync()               ──► Transport.cs
  │
  ├─ SubscribeString
  │    ├─ RegisterStringSubscription(guid, id, index, element, callback)
  │    │    └─ _subscriptionRegistry[guid] = new SubscriptionEntry(...)
  │    └─ SendDataRefSubscribeAsync(id, index)
  │         └─ SubscribeDataRefsAsync(datarefs)
  │              └─ SendWebSocketFireAndForgetAsync()               ──► Transport.cs
  │
  ├─ UnsubscribeByGuid
  │    └─ HandleUnsubscribeByGuidAsync(guid)
  │         ├─ _subscriptionRegistry.TryRemove(guid)
  │         ├─ remove callback from _subscriptions or _stringSubscriptions
  │         └─ if last consumer:
  │              ├─ clean up _subscribedIndices
  │              └─ UnsubscribeDataRefsAsync()                      ──► Transport.cs
  │
  ├─ SubscribeCommandUpdates
  │    ├─ _commandSubscriptions.AddOrUpdate (inline)
  │    └─ SendWebSocketFireAndForgetAsync()                         ──► Transport.cs
  │
  ├─ UnsubscribeAllDataRefs
  │    └─ UnsubscribeAllDataRefsAsync()
  │         ├─ SendWebSocketFireAndForgetAsync()                    ──► Transport.cs
  │         └─ clears: _subscriptions, _stringSubscriptions, _subscribedIndices, _subscriptionRegistry
  │
  └─ UnsubscribeAllCommands
       └─ UnsubscribeAllCommandUpdatesAsync()
            ├─ SendWebSocketFireAndForgetAsync()                    ──► Transport.cs
            └─ clears: _commandSubscriptions
```

---

## Methods in This File (execution order)

| # | Method | Role | Called by |
|---|--------|------|-----------|
| 1 | `DataRefWorkerHandler` | Adapter class bridging Worker → XPlaneWebConnector | Worker loop |
| 2 | `ProcessIncomingMessage` | Entry point for incoming WS data | HandleData (Worker) |
| 3 | `HandleDataRefUpdates` | Parses dataref_update_values | ProcessIncomingMessage |
| 4 | `DispatchScalarUpdate` | Dispatches scalar updates | HandleDataRefUpdates |
| 5 | `DispatchArrayUpdate` | Dispatches array updates | HandleDataRefUpdates |
| 6 | `DispatchStringUpdate` | Dispatches string updates | HandleDataRefUpdates |
| 7 | `HandleCommandUpdates` | Parses command_update_is_active | ProcessIncomingMessage |
| 8 | `RegisterNumericSubscription` | Registers numeric sub + registry entry | HandleWorkerCommandAsync |
| 9 | `RegisterStringSubscription` | Registers string sub + registry entry | HandleWorkerCommandAsync |
| 10 | `HandleWorkerCommandAsync` | Main command dispatcher | HandleCommandAsync (Worker) |
| 11 | `HandleUnsubscribeByGuidAsync` | Ref-counted unsubscribe by GUID | HandleWorkerCommandAsync |
| 12 | `SendDataRefSubscribeAsync` | Tracks index + sends WS subscribe | HandleWorkerCommandAsync |
| 13 | `SubscribeDataRefsAsync` | Sends WS dataref_subscribe_values | SendDataRefSubscribeAsync |
| 14 | `UnsubscribeAllDataRefsAsync` | Sends WS unsubscribe + clears state | HandleWorkerCommandAsync |
| 15 | `UnsubscribeAllCommandUpdatesAsync` | Sends WS unsubscribe + clears state | HandleWorkerCommandAsync |

---

## Cross-File Transitions

| Direction | Target File | Method | Purpose |
|---|---|---|---|
| **inbound** | ← XPlaneWebConnector.cs | `_commandChannel` write | Commands arrive from public API |
| **inbound** | ← Transport.cs | `_dataChannel` write | WS messages arrive from receive loop |
| **outbound** | → Transport.cs | `SendWebSocketFireAndForgetAsync` | Sends WS subscribe/unsubscribe messages |
| **outbound** | → CallbackChannel.cs | `_callbacks.Writer.TryWrite` | Queues callbacks for the callback thread |
| **outbound** | → Utility.cs | `DecodeByteArrayToString` | Decodes byte-array datarefs to strings |
