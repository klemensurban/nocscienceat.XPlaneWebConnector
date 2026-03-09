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
| `_callbacks` | `Channel<Action>` | No (unbounded) | Worker.cs (Dispatch* methods capture value in closure) | CallbackChannel.cs |

---

## Fields & Constructor

```
XPlaneWebConnector(host, port, ...)
  └─ validates apiVersion against SupportedApiVersions
  └─ initializes: _logger, _httpClientFactory, _baseUrl, _wsUrl, _capabilitiesUrl
  └─ initializes channels: _dataChannel, _commandChannel, _callbacks
  └─ initializes caches:   _dataRefIdCache, _reverseDataRefIdCache, _commandIdCache, _reverseCommandIdCache
  └─ initializes subs:     _subscriptions, _subscribedIndices, _subscriptionRegistry, _commandSubscriptions

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
All overloads call `ResolveAndValidateDataRefAsync` which validates type + array support.

```
SubscribeAsync(string path, Action<float>) → IDisposable
  ├─ ResolveAndValidateDataRefAsync(path, IsTypeFloat, supportsArrayIndex: true)
  │    ├─ ParseDataRefPath()                      ──► Utility.cs
  │    ├─ ResolveDataRefIdAsync()                 ── HTTP GET /datarefs  (this file)
  │    └─ Validate type check + array index
  ├─ SubscribeAsyncInternal(path, info, index, onchange)
  │    ├─ Guid.NewGuid()                          ── generates subscription ID
  │    ├─ _commandChannel.Writer.SendCommandAsync ──► Worker.cs (HandleWorkerCommandAsync)
  │    └─ return new SubscriptionHandle(guid)

SubscribeAsync(string path, Action<int>) → IDisposable
  └─ Same flow, validates IsTypeInt, supportsArrayIndex: true

SubscribeAsync(string path, Action<double>) → IDisposable
  └─ Same flow, validates IsTypeDouble, supportsArrayIndex: false
     (double has no array variant in X-Plane)

SubscribeAsync(string path, Action<string>) → IDisposable
  └─ Same flow, validates IsTypeData, supportsArrayIndex: false

SubscribeCommandAsync(string commandPath, Action<bool>) → IDisposable
  ├─ ResolveCommandIdAsync()                 ── HTTP GET /commands  (this file)
  ├─ Guid.NewGuid()                          ── generates subscription ID
  ├─ _commandChannel.Writer.SendCommandAsync ──► Worker.cs
  └─ return new SubscriptionHandle(guid)
```

---

## Set DataRef / Send Command (IXPlaneWebConnector)

```
SetDataRefValueAsync(string path, float value)
  └─ ResolveAndValidateDataRefAsync(path, IsTypeFloat, supportsArrayIndex: true)
       ├─ ParseDataRefPath()                 ──► Utility.cs
       ├─ ResolveDataRefIdAsync()            ── this file
       └─ SetDataRefValueCoreAsync(info, index, jsonValue)
            └─ HTTP transport: SetDataRefValueByHttpAsync()    ──► Api.cs
               WS transport:   SetDataRefValuesByWsAsync()      ──► Api.cs
                                 └─ SendWebSocketFireAndForgetAsync() ──► Transport.cs

SetDataRefValueAsync(string path, int/double/string)
  └─ Same pattern: validates type, calls SetDataRefValueCoreAsync

SendCommandAsync(string commandPath)
  └─ SendCommandAsync(string, duration)
       ├─ ResolveCommandIdAsync()            ── this file
       └─ HTTP transport: ActivateCommandAsync()           ──► Api.cs
          WS transport:   SetCommandActiveAsync()          ──► Api.cs
                            └─ SendWebSocketFireAndForgetAsync() ──► Transport.cs
```

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
| **Api.cs** | `SetDataRefValueByHttpAsync`, `SetDataRefValuesByWsAsync` | `SetDataRefValueAsync` |
| **Api.cs** | `ActivateCommandAsync`, `SetCommandActiveAsync` | `SendCommandAsync` |
| **Transport.cs** | `ConnectWebSocketAndReceiveAsync` | `Start()` |
| **CallbackChannel.cs** | `StartCallbacksAsync` | `Start()` |
| **Worker.cs** | `DataRefWorkerHandler` (ctor) | `Start()` |
| **Worker.cs** | `HandleWorkerCommandAsync` | via command channel from `SubscribeAsync`, `SubscribeCommandAsync` |
| **Utility.cs** | `ParseDataRefPath` | `SubscribeAsync`, `SetDataRefValueAsync` |

### Sibling partial class files (no direct calls, shared state)

| File | Responsibility |
|---|---|
| **Availability.cs** | `IsAvailableAsync`, `WaitUntilAvailableAsync` |
| **Api.cs** | All stateless `IXPlaneApi` implementations (REST + WS sends), `BuildQueryUrl` |
