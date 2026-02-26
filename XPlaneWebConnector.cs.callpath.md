# XPlaneWebConnector.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.cs`
> **Responsibility:** Fields, constructor, lifecycle, high-level public API surface (subscribe, set, send), ID resolution, IDisposable.
> **Thread context:** Caller thread (any thread — typically the DI / hosted-service thread).

---

## Channel Dataflow — This File's Role

This file is the **origin of all three channels** and the **public entry point** for consumers.
It writes to two channels but never reads from any — consumption happens in other partial files.

```
                          ┌─────────────────────────────────────────────────────┐
                          │              XPlaneWebConnector.cs                  │
                          │          (caller / consumer thread)                 │
                          └──────────┬──────────────────────┬───────────────────┘
                                     │                      │
  Consumer calls SubscribeAsync()    │                      │  Consumer calls SetDataRefValueAsync()
  Consumer calls StopAsync()         │                      │  Consumer calls SendCommandAsync()
                                     ▼                      ▼
                          ┌──────────────────┐   ┌────────────────────────┐
                          │  _commandChannel │   │  HTTP / WS direct      │
                          │  (WorkerCommand) │   │  (no channel needed)   │
                          └────────┬─────────┘   └───────────┬────────────┘
                                   │                         │
                                   ▼                         ▼
                          Worker.cs picks up         Transport.cs sends
                          via HandleWorkerCommand    via SendWebSocketFireAndForget
                                   │                         │
                                   └────────┬────────────────┘
                                            ▼
                                       X-Plane
```

### Channels defined in this file

| Channel | Type | Bounded | Writer(s) | Reader |
|---|---|---|---|---|
| `_dataChannel` | `Channel<XPlaneDataMessage>` | Yes (100, DropOldest) | Transport.cs (receive loop) | Worker.cs (HandleData) |
| `_commandChannel` | `Channel<CommandEnvelope<WorkerCommand, bool>>` | No (unbounded) | This file (SubscribeAsync, StopAsync) | Worker.cs (HandleCommandAsync) |
| `_callbacks` | `Channel<CallbackItem>` | No (unbounded) | Worker.cs (Dispatch* methods) | CallbackChannel.cs |

---

## Fields & Constructor

```
XPlaneWebConnector(host, port, ...)
  └─ validates apiVersion against SupportedApiVersions
  └─ initializes: _logger, _httpClientFactory, _baseUrl, _wsUrl, _capabilitiesUrl
  └─ initializes channels: _dataChannel, _commandChannel, _callbacks
  └─ initializes caches:   _dataRefIdCache, _reverseDataRefIdCache, _commandIdCache, _reverseCommandIdCache
  └─ initializes subs:     _subscriptions, _stringSubscriptions, _subscribedIndices, _subscriptionRegistry, _commandSubscriptions

XPlaneWebConnector(IXPlaneWebConnectorSettings, ILogger, IHttpClientFactory)
  └─ DI-friendly constructor — delegates to the raw-parameter constructor above
  └─ parses Transport string → CommandSetDataRefTransport enum (defaults to WebSocket)
```

---

## Lifecycle

```
Start()
  ├─► ConnectWebSocketAndReceiveAsync()      ──► Transport.cs       (receive thread)
  ├─► StartCallbacksAsync()                  ──► CallbackChannel.cs (callback thread)
  └─► Worker.RunAsync()                      ──► Worker.cs          (worker thread)
        uses DataRefWorkerHandler            ──  defined in Worker.cs

StopAsync()
  ├─ cancels all tasks
  ├─ awaits _workerTask shutdown
  ├─ closes WebSocket
  └─ clears subscription dictionaries
```

---

## Public Subscribe API (IXPlaneWebConnector)

Returns `IDisposable` (`SubscriptionHandle`) for per-consumer unsubscription.

```
SubscribeAsync(SimDataRef, callback) → IDisposable
  ├─ ParseDataRefPath()                      ──► Utility.cs
  ├─ ResolveDataRefIdAsync()                 ── HTTP GET /datarefs  (this file)
  ├─ Guid.NewGuid()                          ── generates subscription ID
  ├─ _commandChannel.Writer.SendCommandAsync ──► Worker thread picks up via HandleWorkerCommandAsync
  │                                                ──► Worker.cs
  └─ return new SubscriptionHandle(guid)      ── caller disposes to unsubscribe

SubscribeAsync(SimStringDataRef, callback) → IDisposable
  ├─ ParseDataRefPath()                      ──► Utility.cs
  ├─ ResolveDataRefIdAsync()                 ── HTTP GET /datarefs  (this file)
  ├─ Guid.NewGuid()                          ── generates subscription ID
  ├─ _commandChannel.Writer.SendCommandAsync ──► Worker.cs
  └─ return new SubscriptionHandle(guid)

SubscribeCommandAsync(SimCommand, callback) → IDisposable
  ├─ ResolveCommandIdAsync()                 ── HTTP GET /commands  (this file)
  ├─ Guid.NewGuid()                          ── generates subscription ID
  ├─ _commandChannel.Writer.SendCommandAsync ──► Worker.cs
  └─ return new SubscriptionHandle(guid)
```

---

## Set DataRef / Send Command (IXPlaneWebConnector)

```
SetDataRefValueAsync(SimDataRef, value)
  └─ SetDataRefValueAsync(string, float)
       ├─ ParseDataRefPath()                 ──► Utility.cs
       ├─ ResolveDataRefIdAsync()            ── this file
       └─ HTTP transport: SetDataRefValueByIdAsync()       ── this file
          WS transport:   SetDataRefValuesByWsAsync()      ── this file
                            └─ SendWebSocketFireAndForgetAsync() ──► Transport.cs

SetDataRefValueAsync(SimStringDataRef, value)
  └─ SetDataRefValueAsync(string, string)    ── same pattern as above

SendCommandAsync(SimCommand)
  └─ SendCommandAsync(SimCommand, duration)
       ├─ ResolveCommandIdAsync()            ── this file
       └─ HTTP transport: ActivateCommandAsync()           ── this file
          WS transport:   SetCommandActiveAsync()          ── this file
                            └─ SendWebSocketFireAndForgetAsync() ──► Transport.cs
```

---

## IXPlaneApi Methods (this file — used by high-level API)

```
SetDataRefValueByIdAsync()                   ── HTTP PATCH /datarefs/{id}/value
ActivateCommandAsync()                       ── HTTP POST /command/{id}/activate
```

> Standalone REST methods (`GetCapabilitiesAsync`, `ListDataRefsAsync`,
> `GetDataRefCountAsync`, `GetDataRefValueAsync`, `ListCommandsAsync`,
> `GetCommandCountAsync`, `StartFlightAsync`, `UpdateFlightAsync`,
> `BuildQueryUrl`) are in **Api.cs**.

---

## WebSocket API Methods (IXPlaneApi)

```
SetDataRefValuesByWsAsync()
  └─ SendWebSocketFireAndForgetAsync()       ──► Transport.cs

SubscribeCommandUpdatesAsync()
  └─ for each id: _commandChannel.Writer.SendCommandAsync  ──► Worker.cs
     (creates shim SimCommand, delegates through SubscribeCommand)

UnsubscribeCommandUpdatesAsync()
  └─ for each id: scans _subscriptionRegistry for Command entries
     └─ _commandChannel.Writer.SendCommandAsync  ──► Worker.cs
        (sends UnsubscribeByGuid for matching entries)

SetCommandActiveAsync()
  └─ SendWebSocketFireAndForgetAsync()       ──► Transport.cs
```

Note: `UnsubscribeDataRefsAsync` is now in Worker.cs (called internally by `HandleUnsubscribeByGuidAsync`).

---

## ID Resolution (private helpers)

```
ResolveDataRefIdAsync(path)                  ── HTTP GET /datarefs, caches in _dataRefIdCache
ResolveCommandIdAsync(path)                  ── HTTP GET /commands, caches in _commandIdCache
```

---

## Cross-File Transitions (outbound)

| Target File | Method Called | Trigger |
|---|---|---|
| **Transport.cs** | `ConnectWebSocketAndReceiveAsync` | `Start()` |
| **Transport.cs** | `SendWebSocketFireAndForgetAsync` | any WS send method |
| **CallbackChannel.cs** | `StartCallbacksAsync` | `Start()` |
| **Worker.cs** | `DataRefWorkerHandler` (ctor) | `Start()` |
| **Worker.cs** | `HandleWorkerCommandAsync` | via command channel from `SubscribeAsync`, `SubscribeCommandAsync` |
| **Utility.cs** | `ParseDataRefPath` | `SubscribeAsync`, `SetDataRefValueAsync` |

### Sibling partial class files (no direct calls, shared state)

| File | Responsibility |
|---|---|
| **Availability.cs** | `IsAvailableAsync`, `WaitUntilAvailableAsync` |
| **Api.cs** | Standalone REST queries (capabilities, datarefs, commands, flight), `BuildQueryUrl` |
