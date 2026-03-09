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
| `_callbacks` | **writes** → | `DispatchNumericUpdate`, `DispatchStringUpdate`, `HandleCommandUpdates` (all enqueue `Action` closures) |

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
  │    └─ HandleDataRefUpdates(root)
  │         ├─ JsonValueKind.Number
  │         │    └─ DispatchScalarUpdate(id, jsonElement)
  │         │         └─ _subscriptions[(id, -1)] →
  │         │             DispatchNumericUpdate(id, element, sub)
  │         │             │  switch (sub.CallbackType):
  │         │             ├─ float:  _callbacks.Writer.TryWrite(() => cb((float)val))
  │         │             ├─ int:    _callbacks.Writer.TryWrite(() => cb(intVal))
  │         │             └─ double: _callbacks.Writer.TryWrite(() => cb(doubleVal))
  │         ├─ JsonValueKind.Array
  │         │    └─ DispatchArrayUpdate(id, arrayElement)
  │         │         └─ _subscribedIndices lookup
  │         │              └─ For each position → DispatchNumericUpdate()
  │         │                   └─ _callbacks.Writer.TryWrite(() => cb(val)) ──► CallbackChannel.cs
  │         └─ JsonValueKind.String
  │              └─ DispatchStringUpdate(id, base64)
  │                   └─ _subscriptions[(id, -1)] →
  │                       foreach cb: _callbacks.Writer.TryWrite(() => cb(decoded))
  │
  ├─ type = "command_update_is_active"
  │    └─ HandleCommandUpdates(root)
  │         └─ _commandSubscriptions[id] →
  │             foreach cb: _callbacks.Writer.TryWrite(() => cb(isActive)) ──► CallbackChannel.cs
  │
  └─ type = "result"
       └─ log warning if failed
```

---

## Command Path: HandleCommand → HandleWorkerCommandAsync

```
HandleWorkerCommandAsync(WorkerCommand, ct)
│
├─ SubscribeDataRef (dispatches by callback type: Action<float>, <int>, <double>, <string>)
│    ├─ RegisterSubscription<T>(guid, dataRefInfo, index, Action<T>)
│    │    ├─ _subscriptions[(id, index)] = SubscriptionCallbacks<T>
│    │    └─ _subscriptionRegistry[guid] = new SubscriptionEntry(...)
│    └─ SendDataRefSubscribeAsync(id, index)
│         └─ SubscribeDataRefsAsync(datarefs)              ──► Api.cs
│              └─ SendWebSocketFireAndForgetAsync()         ──► Transport.cs
│
├─ SubscribeCommand
│    ├─ RegisterCommandSubscription(guid, id, commandPath, callback)
│    │    └─ _subscriptionRegistry[guid] = new SubscriptionEntry(..., Command)
│    └─ SendCommandSubscribeAsync(id)
│         └─ SubscribeCommandUpdatesAsync([id])           ──► Api.cs
│              └─ SendWebSocketFireAndForgetAsync()        ──► Transport.cs
│
├─ UnsubscribeByGuid
│    └─ HandleUnsubscribeByGuidAsync(guid)
│         ├─ _subscriptionRegistry.Remove(guid)
│         ├─ remove callback from _subscriptions or _commandSubscriptions
│         │    (via SubscriptionCallbacks.RemoveCallback polymorphic dispatch)
│         └─ if last consumer:
│              ├─ Data: clean up _subscribedIndices
│              │    └─ UnsubscribeDataRefsAsync()                  ──► Api.cs
│              └─ Command: SendCommandUnsubscribeAsync()          ──► Api.cs
│                   └─ UnsubscribeCommandUpdatesAsync([id])       ──► Transport.cs
│
├─ UnsubscribeAllDataRefs
│    └─ UnsubscribeAllDataRefsAsync()
│         ├─ SendWebSocketFireAndForgetAsync()                    ──► Transport.cs
│         └─ clears: _subscriptions, _subscribedIndices
│                  + Data entries from _subscriptionRegistry
│
└─ UnsubscribeAllCommands
     └─ UnsubscribeAllCommandUpdatesAsync()
          ├─ SendWebSocketFireAndForgetAsync()                    ──► Transport.cs
          └─ clears: _commandSubscriptions
                   + Command entries from _subscriptionRegistry
```

---

## Methods in This File (execution order)

| # | Method | Role | Called by |
|---|--------|------|-----------|
| 1 | `DataRefWorkerHandler` | Adapter class bridging Worker → XPlaneWebConnector | Worker loop |
| 2 | `ProcessIncomingMessage` | Entry point for incoming WS data | HandleData (Worker) |
| 3 | `HandleDataRefUpdates` | Parses dataref_update_values | ProcessIncomingMessage |
| 4 | `DispatchNumericUpdate` | Type-aware dispatch for float/int/double | DispatchScalarUpdate, DispatchArrayUpdate |
| 5 | `DispatchScalarUpdate` | Dispatches scalar updates | HandleDataRefUpdates |
| 6 | `DispatchArrayUpdate` | Dispatches array updates | HandleDataRefUpdates |
| 7 | `DispatchStringUpdate` | Dispatches string/data updates | HandleDataRefUpdates |
| 8 | `HandleCommandUpdates` | Parses command_update_is_active | ProcessIncomingMessage |
| 9 | `HandleWorkerCommandAsync` | Main command dispatcher | HandleCommandAsync (Worker) |
| 10 | `RegisterSubscription<T>` | Registers typed subscription + registry entry | HandleWorkerCommandAsync |
| 11 | `RegisterCommandSubscription` | Registers command sub + registry entry | HandleWorkerCommandAsync |
| 12 | `HandleUnsubscribeByGuidAsync` | Ref-counted unsubscribe by GUID (Data/Command) | HandleWorkerCommandAsync |
| 13 | `SendDataRefSubscribeAsync` | Tracks index + delegates to `SubscribeDataRefsAsync` (Api.cs) | HandleWorkerCommandAsync |
| 14 | `SendCommandSubscribeAsync` | Delegates to `SubscribeCommandUpdatesAsync` (Api.cs) | HandleWorkerCommandAsync |
| 15 | `SendCommandUnsubscribeAsync` | Delegates to `UnsubscribeCommandUpdatesAsync` (Api.cs) | HandleUnsubscribeByGuidAsync |
| 16 | `UnsubscribeAllDataRefsAsync` | Sends WS unsubscribe + clears dataref state | HandleWorkerCommandAsync |
| 17 | `UnsubscribeAllCommandUpdatesAsync` | Sends WS unsubscribe + clears command state | HandleWorkerCommandAsync |

---

## Cross-File Transitions

| Direction | Target File | Method | Purpose |
|---|---|---|---|
| **inbound** | ← XPlaneWebConnector.cs | `_commandChannel` write | Commands arrive from public API |
| **inbound** | ← Transport.cs | `_dataChannel` write | WS messages arrive from receive loop |
| **outbound** | → Api.cs | `SubscribeDataRefsAsync`, `UnsubscribeDataRefsAsync` | WS subscribe/unsubscribe datarefs |
| **outbound** | → Api.cs | `SubscribeCommandUpdatesAsync`, `UnsubscribeCommandUpdatesAsync` | WS subscribe/unsubscribe commands |
| **outbound** | → Transport.cs | `SendWebSocketFireAndForgetAsync` | Sends WS unsubscribe-all messages |
| **outbound** | → CallbackChannel.cs | `_callbacks.Writer.TryWrite` | Queues Action closures for the callback thread |
