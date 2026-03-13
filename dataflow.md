# nocscienceat.XPlaneWebConnector — Dataflow & Architecture Reference

This document provides a detailed description of the internal data flows, threading model, and protocol interactions within the `nocscienceat.XPlaneWebConnector` library. It is intended for developers who want to understand, maintain, or extend the library.

---

## Table of Contents

1. [Library Overview](#1-library-overview)
2. [Interface Layering](#2-interface-layering)
3. [Type System](#3-type-system)
4. [Lifecycle & Connection Management](#4-lifecycle--connection-management)
5. [Outbound Dataflow: Consumer → X-Plane](#5-outbound-dataflow-consumer--x-plane)
6. [Inbound Dataflow: X-Plane → Consumer](#6-inbound-dataflow-x-plane--consumer)
7. [ID Resolution & Caching](#7-id-resolution--caching)
8. [Threading Model & Concurrency](#8-threading-model--concurrency)
9. [WebSocket Protocol Details](#9-websocket-protocol-details)
10. [REST API Details](#10-rest-api-details)
11. [Error Handling & Reconnection](#11-error-handling--reconnection)
12. [Real-World Consumer Example](#12-real-world-consumer-example)
13. [Internal State & Data Structures](#13-internal-state--data-structures)
14. [Design Decisions & Trade-offs](#14-design-decisions--trade-offs)

---

## 1. Library Overview

`nocscienceat.XPlaneWebConnector` is a .NET 10 library that communicates with X-Plane 12.1.1+ via its built-in REST and WebSocket APIs. The API version path segment (`v1`, `v2`, `v3`) is configurable at construction time to match your X-Plane version.

The library provides two abstraction levels:

- **High-level** (`IXPlaneWebConnector`): Subscribe to datarefs and command activation status with callbacks (returns `IDisposable` for per-consumer unsubscription), set values by path, send commands by name, wait for X-Plane availability, detect connection loss. The library handles name→ID resolution, WebSocket framing, and JSON serialization transparently.
- **Low-level** (`IXPlaneApi`): Direct access to the X-Plane REST endpoints and raw WebSocket operations using session IDs.

### System Context

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Consumer Application                         │
│                                                                     │
│  ┌───────────────┐  ┌───────────────────┐  ┌─────────────────────┐  │
│  │ Panel Handler │  │ Hosted Service    │  │ Custom Components   │  │
│  │ (hardware I/O)│  │ (lifecycle mgmt)  │  │ (your code)         │  │
│  └───────────────┘  └───────────────────┘  └─────────────────────┘  │
│         │                   │                        │              │
│         └───────────────────┼────────────────────────┘              │
│                             │                                       │
│                ┌────────────┴────────────┐                          │
│                │   IXPlaneWebConnector   │ High-level API           │
│                │   IXPlaneApi            │ Low-level API            │
│                └─────────────────────────┘                          │
└─────────────────────────────────────────────────────────────────────┘
                              │
              ┌───────────────┼────────────────┐
              │ HTTP (REST)   │ WebSocket      │
              │               │                │
              │               │                │
┌─────────────────────────────────────────────────────────────────────┐
│                      X-Plane 12.1.1+                                │
│                                                                     │
│  /api/{version}/datarefs  /api/{version}/commands                   │
│  /api/{version}/flight    /api/capabilities                         │
│  ws://host:port/api/{version}                                       │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 2. Interface Layering

The two interfaces are implemented by a single class (`XPlaneWebConnector`) but serve different roles:

```
                    ┌───────────────────────────────┐
                    │      XPlaneWebConnector       │
                    │   (sealed partial class)      │
                    ├───────────────────────────────┤
                    │ implements:                   │
                    │  • IXPlaneWebConnector        │
                    │  • IXPlaneApi                 │
                    │  • IDisposable                │
                    └───────────────────────────────┘

 ┌─────────────────────────┐  ┌──────────────────────────┐
 │  IXPlaneWebConnector    │  │   IXPlaneApi             │
 │  (high-level)           │  │   (low-level)            │
 ├─────────────────────────┤  ├──────────────────────────┤
 │ Start()                 │  │ GetCapabilitiesAsync()   │
 │ StopAsync()             │  │ ListDataRefsAsync()      │
 │ IsAvailableAsync()      │  │ GetDataRefCountAsync()   │
 │ WaitUntilAvailableAs()  │  │ GetDataRefValueAsync()   │
 │ ConnectionClosed event  │  │ SetDataRefValueByIdAs()  │
 │ SubscribeAsync()        │  │ SetDataRefValuesByWs()   │
 │  → returns IDisposable  │  │ ListCommandsAsync()      │
 │ SubscribeCommandAsync() │  │ GetCommandCountAsync()   │
 │  → returns IDisposable  │  │ ActivateCommandAsync()   │
 │ SetDataRefValueAsync()  │  │ StartFlightAsync()       │
 │ SendCommandAsync()      │  │ UpdateFlightAsync()      │
 │ SendCommandAsync(,dur)  │  │ SubscribeDataRefsAsync() │
 │ Dispose()               │  │ UnsubscribeDataRefsAs()  │
 └─────────────────────────┘  │ UnsubscribeAllDataRefs() │
                              │ Subscribe/Unsub CmdUpd() │
                              │ SetCommandActiveAsync()  │
                              └──────────────────────────┘
```

### When to use which interface

| Use case | Interface | Example |
|---|---|---|
| Subscribe to a dataref with a callback | `IXPlaneWebConnector` | Panel LED updates |
| Subscribe to command activation status | `IXPlaneWebConnector` | Engine master monitoring |
| Unsubscribe a single consumer | Dispose the `IDisposable` handle | Panel shutdown |
| Set a switch position by dataref path | `IXPlaneWebConnector` | Toggle landing lights |
| Send a one-shot command by name | `IXPlaneWebConnector` | APU master on |
| Query all registered datarefs | `IXPlaneApi` | Tooling / debugging |
| Batch-set multiple datarefs in one WS frame | `IXPlaneApi` | High-frequency syncing |
| Control command begin/end precisely | `IXPlaneApi` | Long-press fire test |
| Wait for X-Plane to start up | `IXPlaneWebConnector` | Hosted service startup |
| React to X-Plane shutdown | `IXPlaneWebConnector` | Graceful app exit |

---

## 3. Type System

The library uses typed callbacks and string paths for consumer-facing dataref and command references. There are no wrapper types — subscriptions and writes operate directly on string paths with the appropriate C# type.

### Supported Dataref Types

| X-Plane `value_type` | C# `Action<T>` / setter type | Array support | Notes |
|---|---|---|---|
| `float` | `float` | `float_array` (scalar + array) | Most common |
| `int` | `int` | `int_array` (scalar + array) | |
| `double` | `double` | Scalar only | No `double_array` in X-Plane |
| `data` | `string` | Scalar only | Base64-encoded bytes; no array variant |

The consumer **must** use the correct type for each dataref. Using the wrong type throws `InvalidOperationException`.

### Internal type metadata: `XPlaneDataRefInfo`

```
  XPlaneDataRefInfo         Resolved from REST: GET /datarefs?filter[name]=...
       Id: long             Session-scoped numeric ID
       Name: string         Fully qualified dataref name
       ValueType: string    "float", "int", "double", "float_array", "int_array", "data"
       IsTypeFloat: bool    Derived from ValueType
       IsTypeInt: bool
       IsTypeDouble: bool
       IsTypeData: bool
       IsArrayType: bool    true when ValueType ends with "_array"
```

**Key property: DataRef path format**

The dataref path supports array indexing via bracket notation. The library parses this internally:

```
"sim/cockpit/autopilot/heading"        → basePath = "sim/.../heading",   index = -1 (scalar)
"AirbusFBW/Foo[7]"                     → basePath = "AirbusFBW/Foo",     index = 7  (array)
"sim/cockpit2/engine/indicators/N1[0]" → basePath = "sim/.../N1",        index = 0  (array)
```

The `ParseDataRefPath` method uses a source-generated regex to extract base path and index.

### Commands

Commands are referenced by string path (e.g. `"sim/autopilot/heading_up"`). The connector resolves the path to a session-scoped ID via REST and caches it. There is no formal `Command` type in the connector API — consumer-side panels may use their own helper types (e.g. `SimCommand`) but the connector accepts plain strings.

---

## 4. Lifecycle & Connection Management

### Startup Sequence

```
Consumer                        Library                          X-Plane
   │                               │                               │
   │  new XPlaneWebConnector(...)  │                               │
   │──────────────────────────────>│  (constructor: saves config)  │
   │                               │                               │
   │  WaitUntilAvailableAsync()    │                               │
   │──────────────────────────────>│                               │
   │                               │───── GET /api/capabilities ──>│
   │                               │<──── 200 OK ──────────────────│
   │                               │                               │
   │                               │  (Phase 2: readiness probe)   │
   │                               │───── GET /datarefs?filter ───>│
   │                               │<──── { data: [{id: ...}] } ───│
   │                               │                               │
   │  Start()                      │                               │
   │──────────────────────────────>│  Creates CancellationToken    │
   │                               │  Launches:                    │
   │                               │   ConnectWebSocketAndReceive  │
   │                               │   StartCallbacksAsync         │
   │                               │   Worker.RunAsync             │
   │                               │                               │
   │                               │──── ws:// CONNECT ───────────>│
   │                               │<── WebSocket OPEN ────────────│
   │                               │                               │
   │  SubscribeAsync(dataref, cb)  │                               │
   │──────────────────────────────>│  (see §5 and §7 for details)  │
   │                               │                               │
```

### Shutdown Sequence

```
Consumer                        Library                          X-Plane
   │                               │                               │
   │  StopAsync(timeout)           │                               │
   │──────────────────────────────>│                               │
   │                               │  CancellationToken.Cancel()   │
   │                               │  await _receiveTask           │
   │                               │    (with timeout)             │
   │                               │                               │
   │                               │──── ws:// CLOSE ─────────────>│
   │                               │<── CLOSE ACK ─────────────────│
   │                               │                               │
   │                               │  Clear subscriptions          │
   │                               │  Clear caches                 │
   │                               │                               │
   │  Dispose()                    │                               │
   │──────────────────────────────>│  Dispose CTS, WebSocket       │
   │                               │                               │
```

### Server-Initiated Shutdown

When X-Plane exits or closes the WebSocket:

```
X-Plane                          Library                       Consumer
   │                               │                               │
   │──── WebSocket CLOSE ─────────>│                               │
   │                               │  ReceiveLoopAsync detects     │
   │                               │  MessageType.Close            │
   │                               │                               │
   │                               │  ConnectionClosed?.Invoke()   │
   │                               │──────────────────────────────>│
   │                               │                               │  (consumer reacts:
   │                               │                               │   e.g. StopApplication)
```

---

## 5. Outbound Dataflow: Consumer → X-Plane

There are four outbound paths, each with a different protocol and purpose.

### 5.1 Subscribe to Dataref (High-Level)

```
Consumer                         Library                                       X-Plane
   │                                │                                             │
   │ SubscribeAsync(path, Action<T>)│                                             │
   │───────────────────────────────>│                                             │
   │                                │                                             │
   │                                │  ResolveAndValidateDataRefAsync(path)       │
   │                                │  ├─ ParseDataRefPath("AirbusFBW/Foo[7]")    │
   │                                │  │  → basePath="AirbusFBW/Foo", index=7     │
   │                                │  ├─ ResolveDataRefIdAsync(basePath)         │
   │                                │  │  ├─ cache hit? → return XPlaneDataRefInfo│
   │                                │  │  └─ cache miss:                          │
   │                                │  │     GET /api/v3/datarefs                 │
   │                                │  │       ?filter[name]=AirbusFBW/Foo ──────>│
   │                                │  │     ← { data: [{ id: 42, ...}] } ────────│
   │                                │  │     cache[path] = XPlaneDataRefInfo      │
   │                                │  └─ Validate: type check + array support    │
   │                                │                                             │
   │                                │  _commandChannel → Worker thread:           │
   │                                │    RegisterSubscription<T>(guid, info, 7,   │
   │                                │      Action<T>)                             │
   │                                │    _subscriptions[(42, 7)] =                │
   │                                │      SubscriptionCallbacks<T>               │
   │                                │    _subscribedIndices[42].Add(7)            │
   │                                │    _subscriptionRegistry[guid] = entry      │
   │                                │                                             │
   │                                │  WS → { req_id: N,                          │
   │                                │         type: "dataref_subscribe_values",   │
   │                                │         params: { datarefs: [               │
   │                                │           { id: 42, index: 7 }              │
   │                                │         ]}} ───────────────────────────────>│
   │                                │                                             │
```

### 5.1b Subscribe to Command Activation (High-Level)

Follows the same flow as dataref subscriptions: resolve name → ID, register in `_commandSubscriptions` + `_subscriptionRegistry`, send WS `command_subscribe_is_active`, return `IDisposable` handle.

```
Consumer                                Library                                      X-Plane
   │                                       │                                            │
   │ SubscribeCommandAsync(path, Action<   │                                            │
   │   bool>)                              │                                            │
   │──────────────────────────────────────>│                                            │
   │                                       │                                            │
   │                                       │  ResolveCommandIdAsync("toliss/.../Mast")  │
   │                                       │  ├─ cache hit? → return id                 │
   │                                       │  └─ cache miss:                            │
   │                                       │     GET /api/v3/commands                   │
   │                                       │       ?filter[name]=toliss/...             │
   │                                       │       &fields=id,name ────────────────────>│
   │                                       │     ← { data: [{ id: 200 }] } ─────────────│
   │                                       │     cache[path] = 200                      │
   │                                       │                                            │
   │                                       │  _commandSubscriptions[200] =              │
   │                                       │     (commandPath, [cb])                    │
   │                                       │  _subscriptionRegistry[guid] =             │
   │                                       │     SubscriptionEntry(200, -1, Command, cb)│
   │                                       │                                            │
   │                                       │  WS → { req_id: N,                         │
   │                                       │     type: "command_subscribe_is_active",   │
   │                                       │     params: { commands: [                  │
   │                                       │       { id: 200 }                          │
   │                                       │     ]}} ──────────────────────────────────>│
   │                                       │                                            │
   │  ← IDisposable (SubscriptionHandle)   │                                            │
```

### 5.2 Set Dataref Value (High-Level)

The transport used is determined by the `CommandSetDataRefTransport` passed to the constructor.

#### Transport: WebSocket (default)

```
Consumer                                 Library                          X-Plane
   │                                        │                                │
   │ SetDataRefValueAsync("path[3]", 1.0f)  │                                │
   │───────────────────────────────────────>│                                │
   │                                        │  ParseDataRefPath → (path, 3)  │
   │                                        │  ResolveDataRefIdAsync(path)   │
   │                                        │  → id (from cache or REST)     │
   │                                        │                                │
   │                                        │  WS → { req_id: N,             │
   │                                        │         type: "dataref_set_    │
   │                                        │               values",         │
   │                                        │         params: { datarefs: [  │
   │                                        │           { id: 42,            │
   │                                        │             value: 1.0,        │
   │                                        │             index: 3 }         │
   │                                        │         ]}} ──────────────────>│
   │                                        │                                │
```

#### Transport: Http

```
Consumer                                 Library                          X-Plane
   │                                        │                                │
   │ SetDataRefValueAsync("path[3]", 1.0f)  │                                │
   │───────────────────────────────────────>│                                │
   │                                        │  ParseDataRefPath → (path, 3)  │
   │                                        │  ResolveDataRefIdAsync(path)   │
   │                                        │  → id (from cache or REST)     │
   │                                        │                                │
   │                                        │  SetDataRefValueByIdAsync()    │
   │                                        │  ├─ [fireForget=false]         │
   │                                        │  │  PATCH /datarefs/42/value   │
   │                                        │  │    ?index=3                 │
   │                                        │  │  ───────────────────────>   │
   │                                        │  │  ←── 200 OK ────────────────│
   │                                        │  │                             │
   │                                        │  └─ [fireForget=true]          │
   │                                        │     _ = FireAndForgetAsync()   │
   │                                        │     return immediately         │
   │                                        │     (errors logged, not thrown)│
   │                                        │                                │
```

String datarefs follow the same path but base64-encode the value before sending.

### 5.3 Send Command (High-Level)

The transport used is determined by the `CommandSetDataRefTransport` passed to the constructor.
The `SendCommandAsync(path)` overload sends with `duration = 0` (press & release).
The `SendCommandAsync(path, duration)` overload allows holding the command for 0–10 seconds.

#### Transport: WebSocket (default)

```
Consumer                                 Library                          X-Plane
   │                                        │                                │
   │ SendCommandAsync("sim/.../gear_toggle")│                                │
   │───────────────────────────────────────>│                                │
   │                                        │  ResolveCommandIdAsync(name)   │
   │                                        │  → id (from cache or REST)     │
   │                                        │                                │
   │                                        │  WS → { req_id: N,             │
   │                                        │         type: "command_set_    │
   │                                        │               is_active",      │
   │                                        │         params: { commands: [  │
   │                                        │           { id: 100,           │
   │                                        │             is_active: true,   │
   │                                        │             duration: 0 }      │
   │                                        │         ]}} ──────────────────>│
   │                                        │                                │
```

#### Transport: Http

```
Consumer                                Library                          X-Plane
   │                                       │                                │
   │ SendCommandAsync("sim/...", 0.5f)     │                                │
   │──────────────────────────────────────>│                                │
   │                                       │  ResolveCommandIdAsync(name)   │
   │                                       │  → id (from cache or REST)     │
   │                                       │                                │
   │                                       │  ActivateCommandAsync()        │
   │                                       │  ├─ [fireForget=false]         │
   │                                       │  │  POST /command/100/activate │
   │                                       │  │  Body: { "duration": 0.5 }  │
   │                                       │  │  ──────────────────────>    │
   │                                       │  │  ←── 200 OK ────────────────│
   │                                       │  │                             │
   │                                       │  └─ [fireForget=true]          │
   │                                       │     _ = FireAndForgetAsync()   │
   │                                       │     return immediately         │
   │                                       │                                │
```

### 5.4 Outbound Summary

```
  ┌─────────────────────────────────────────────────────────────────────────┐
  │                    Outbound Data Paths                                  │
  │    (transport selected by CommandSetDataRefTransport)                   │
  ├─────────────────────────────────────────────────────────────────────────┤
  │                                                                         │
  │ Consumer ──> IXPlaneWebConnector                                        │
  │              │                                                          │
  │              ├── SubscribeAsync()                                       │
  │              │     ├─ ResolveDataRefIdAsync() ──── REST GET ──> X-Plane │
  │              │     └─ SendDataRefSubscribeAsync() ── WS ──────> X-Plane │
  │              │                                                          │
  │              ├── SubscribeCommandAsync()                                │
  │              │     ├─ ResolveCommandIdAsync() ──── REST GET ──> X-Plane │
  │              │     └─ SendCommandSubscribeAsync() ── WS ──────> X-Plane │
  │              │                                                          │
  │              ├── SetDataRefValueAsync()                                 │
  │              │     ├─ ResolveDataRefIdAsync() ──── REST GET ──> X-Plane │
  │              │     ├─ [WebSocket]  SetDataRefValuesByWsAsync() ─ WS ──> │
  │              │     └─ [Http]       SetDataRefValueByHttpAsync()         │
  │              │                      └─ PATCH /datarefs/{id}/value ───>  │
  │              │                                                          │
  │              └── SendCommandAsync()                                     │
  │                    ├─ ResolveCommandIdAsync() ──── REST GET ──> X-Plane │
  │                    ├─ [WebSocket]  SetCommandActiveAsync() ── WS ────>  │
  │                    └─ [Http]       ActivateCommandAsync()               │
  │                                    └─ POST /command/{id}/activate ───>  │
  │                                                                         │
  │ Consumer ──> IXPlaneApi                                                 │
  │              │                                                          │
  │              ├── ListDataRefsAsync() ─────────── REST GET ────> X-Plane │
  │              ├── SetDataRefValueByHttpAsync() ── REST PATCH ──> X-Plane │
  │              ├── SetDataRefValuesByWsAsync() ─── WS ──────────> X-Plane │
  │              ├── ActivateCommandAsync() ──────── REST POST ───> X-Plane │
  │              ├── SetCommandActiveAsync() ─────── WS ──────────> X-Plane │
  │              ├── StartFlightAsync() ──────────── REST POST ──> X-Plane  │
  │              └── UpdateFlightAsync() ─────────── REST PATCH ──> X-Plane │
  │                                                                         │
  │      All WebSocket sends go through SendWebSocketFireAndForgetAsync()   │
  │       → JSON serialize with source-generated context                    │
  │       → ClientWebSocket.SendAsync()                                     │
  │       → No acknowledgement waiting                                      │
  │                                                                         │
  │      When fireForgetOnHttpTransport = true, all HTTP write operations   │
  │       (SetDataRefValueByIdAsync, ActivateCommandAsync,                  │
  │        StartFlightAsync, UpdateFlightAsync) use the same pattern:       │
  │       → _ = FireAndForgetAsync() (async local function)                 │
  │       → Return immediately; errors caught and logged as warnings        │
  │       → HttpResponseMessage disposed via using declaration              │
  └─────────────────────────────────────────────────────────────────────────┘
```

---

## 6. Inbound Dataflow: X-Plane → Consumer

Inbound data arrives exclusively via WebSocket. The library uses a **three-stage pipeline** to decouple fast WebSocket reads from JSON processing and potentially slow callback execution:

### 6.1 Pipeline Overview

```
X-Plane                    Library                                      Consumer
   │                          │                                            │
   │  WS frame                │                                            │
   │  (dataref_update_values) │                                            │
   │─────────────────────────>│                                            │
   │                          │                                            │
   │                          │  ┌─────────────────────────────────────┐   │
   │                          │  │ Stage 1: ReceiveLoopAsync           │   │
   │                          │  │ (Thread: WS receive task)           │   │
   │                          │  │                                     │   │
   │                          │  │ • Read complete WS message          │   │
   │                          │  │ • Assemble multi-frame messages     │   │
   │                          │  │ • Detect Close frames               │   │
   │                          │  │ • Enqueue raw byte[] into Channel   │   │
   │                          │  │   (bounded, DropOldest if full)     │   │
   │                          │  └─────────────────────────────────────┘   │
   │                          │                 │                          │
   │                          │                 ▼                          │
   │                          │  ┌──────────────────────────────────┐      │
   │                          │  │ _dataChannel (capacity: 100)     │      │
   │                          │  │ BoundedChannelFullMode.DropOldest│      │
   │                          │  │ SingleReader = true              │      │
   │                          │  └──────────────────────────────────┘      │
   │                          │                 │                          │
   │                          │                 ▼                          │
   │                          │  ┌─────────────────────────────────────┐   │
   │                          │  │ Stage 2: Worker (HandleData)        │   │
   │                          │  │ (Thread: Worker task)               │   │
   │                          │  │                                     │   │
   │                          │  │ • JSON parse (JsonDocument)         │   │
   │                          │  │ • Dispatch by message type          │   │
   │                          │  │ • Look up registered callbacks      │   │
   │                          │  │ • Capture value in Action closure   │   │
   │                          │  │ • Enqueue Action into _callbacks    │   │
   │                          │  │   (non-blocking TryWrite)           │   │
   │                          │  └─────────────────────────────────────┘   │
   │                          │                 │                          │
   │                          │                 ▼                          │
   │                          │  ┌──────────────────────────────────┐      │
   │                          │  │ _callbacks (unbounded)           │      │
   │                          │  │ Channel<Action>                  │      │
   │                          │  │ SingleReader = true              │      │
   │                          │  └──────────────────────────────────┘      │
   │                          │                 │                          │
   │                          │                 ▼                          │
   │                          │  ┌─────────────────────────────────────┐   │
   │                          │  │ Stage 3: StartCallbacksAsync        │   │
   │                          │  │ (Thread: callback task)             │   │
   │                          │  │                                     │   │
   │                          │  │ • Invokes Action() directly         │   │
   │                          │  │ • Sequential execution              │   │
   │                          │  └─────────────────────────────────────┘   │
   │                          │                        │                   │
   │                          │                        ▼                   │
   │                          │            Action() → cb(typedValue) ─────>│
   │                          │                                            │
```

### 6.2 Message Type Routing

Stage 2 routes messages based on the `type` field in the JSON:

```
ProcessIncomingMessage(byte[])
   │
   ├── type == "dataref_update_values"
   │   └── HandleDataRefUpdates()
   │       │
   │       ├── data[id] is Number (scalar float, int, or double)
   │       │   └── DispatchScalarUpdate(id, JsonElement)
   │       │       └── _subscriptions[(id, -1)] →
   │       │           DispatchNumericUpdate(id, element, sub)
   │       │           │
   │       │           ├─ sub.CallbackType == float
   │       │           │   └─ _callbacks.Writer.TryWrite(() => cb((float)val))
   │       │           ├─ sub.CallbackType == int
   │       │           │   └─ _callbacks.Writer.TryWrite(() => cb(intVal))
   │       │           └─ sub.CallbackType == double
   │       │               └─ _callbacks.Writer.TryWrite(() => cb(doubleVal))
   │       │
   │       ├── data[id] is Array (float_array or int_array)
   │       │   └── DispatchArrayUpdate(id, JsonElement)
   │       │       │
   │       │       └── _subscribedIndices[id] exists?
   │       │           └── For each position in sorted index set:
   │       │               └── _subscriptions[(id, idx)] →
   │       │                   DispatchNumericUpdate(id, element, sub)
   │       │
   │       └── data[id] is String (base64-encoded data type)
   │           └── DispatchStringUpdate(id, base64)
   │               └── Base64 decode → _subscriptions[(id, -1)] →
   │                   foreach cb: _callbacks.Writer.TryWrite(() => cb(string))
   │
    ├── type == "command_update_is_active"
    │   └── HandleCommandUpdates()
    │       └── For each command ID in data:
    │           └── _commandSubscriptions[id] →
    │               foreach cb: _callbacks.Writer.TryWrite(() => cb(isActive))
   │
   └── type == "result"
       └── Deserialize WsResultMessage
           └── If !Success → log warning with error code and message
```

### 6.3 Array Dataref Dispatch Detail

X-Plane sends array values only for subscribed indices, in sorted order. The library must map positional values back to the original indices:

```
Example: Consumer subscribes to indices [3, 7, 1]

  _subscribedIndices[42] = SortedSet { 1, 3, 7 }   (sorted!)

  X-Plane sends:  { "42": [0.5, 1.2, 3.4] }
                            ^    ^    ^
                            │    │    └── position 2 → index 7
                            │    └─────── position 1 → index 3
                            └──────────── position 0 → index 1

  Dispatch:
    _subscriptions[(42, 1)].Callback(element, 0.5)
    _subscriptions[(42, 3)].Callback(element, 1.2)
    _subscriptions[(42, 7)].Callback(element, 3.4)
```

### 6.4 String Dataref Decoding

String/data-type datarefs arrive as base64-encoded strings:

```
Format: Base64 string
  { "42": "SEVMTE8=" }
  → Base64 decode → byte[] → trim trailing null bytes → UTF-8 string "HELLO"

  Handled by DispatchStringUpdate(id, base64):
  1. Convert.FromBase64String(base64) → byte[]
  2. Array.IndexOf(bytes, 0) → find first null terminator
  3. Encoding.UTF8.GetString(bytes, 0, len) → decoded string
  4. Enqueue: _callbacks.Writer.TryWrite(() => cb(decoded))
```

If base64 decoding fails, the raw string is passed through as a fallback.

---

## 7. ID Resolution & Caching

X-Plane's WebSocket API uses numeric session IDs, not string paths. The library resolves names to IDs via REST and caches the full `XPlaneDataRefInfo` (which includes `Id`, `Name`, `ValueType`, and derived type flags):

```
┌─────────────────────────────────────────────────────────────────────┐
│                       ID Resolution Flow                            │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Consumer calls:                                                    │
│    SubscribeAsync("sim/.../heading", (float v) => ...)              │
│                                                                     │
│         │                                                           │
│         ▼                                                           │
│  ┌──────────────────────┐                                           │
│  │  _dataRefIdCache     │                                           │
│  │  ConcurrentDictionary│     ┌─── hit ──> return XPlaneDataRefInfo │
│  │  <string,            │─────┤                                     │
│  │   XPlaneDataRefInfo> │     └─── miss ──────┐                     │
│  └──────────────────────┘                     │                     │
│                                               │                     │
│                                               ▼                     │
│                                  ┌─────────────────────────┐        │
│                                  │ REST: GET /api/v3/      │        │
│                                  │  datarefs?filter[name]= │        │
│                                  │  {path}&fields=id,name  │        │
│                                  └─────────────────────────┘        │
│                                               │                     │
│                                               ▼                     │
│                                  ┌───────────────────────────┐      │
│                                  │ Parse response:           │      │
│                                  │ { data: [{ id: 42,        │      │
│                                  │   value_type: "float" }] }│      │
│                                  │                           │      │
│                                  │ _dataRefIdCache[path] =   │      │
│                                  │   XPlaneDataRefInfo       │      │
│                                  │ return info               │      │
│                                  └───────────────────────────┘      │
│                                                                     │
│  Same flow for _commandIdCache via ResolveCommandIdAsync()          │
│  (commands cache only the long id, not a metadata object)           │
│                                                                     │
│  → Cache is NOT cleared on reconnect — IDs are session-scoped       │
│    in X-Plane but in practice remain stable within a session.       │
│    Caches are cleared on StopAsync().                               │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

**Performance implication:** The first time any given dataref or command is used, a synchronous REST call is made. Subsequent uses hit the in-memory `ConcurrentDictionary`. During X-Plane startup, the REST API can be slow (the simulator's main thread is saturated with loading), which can cause multi-second delays on first use.

**Mitigation for consumers:** Call `SubscribeAsync()` for all datarefs during startup (which resolves and caches all dataref IDs). For commands, consider resolving them eagerly at startup if instant response to button presses is required.

---

## 8. Threading Model & Concurrency

```
┌────────────────────────────────────────────────────────────────────────┐
│                       Thread / Task Map                                │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Task 1: ConnectWebSocketAndReceiveAsync  (background, long-lived)     │
│  ├── Owns the ClientWebSocket lifecycle                                │
│  ├── Runs ReceiveLoopAsync in a tight loop                             │
│  ├── Handles reconnection on failure                                   │
│  └── Writes raw byte[] into _dataChannel                               │
│                                                                        │
│  Task 2: Worker (HandleData + HandleCommandAsync)  (long-lived)        │
│  ├── Reads from _dataChannel and _commandChannel                       │
│  ├── Parses JSON and dispatches to subscription dictionaries           │
│  ├── Enqueues Action closures into _callbacks channel (non-blocking)   │
│  └── Never blocked by slow consumer callbacks                          │
│                                                                        │
│  Task 3: StartCallbacksAsync  (background, long-lived)                 │
│  ├── Reads from _callbacks channel                                     │
│  ├── Invokes consumer callbacks sequentially                           │
│  └── A slow callback blocks other callbacks, but NOT the Worker        │
│      → Consumers with heavy processing should offload to own queue     │
│                                                                        │
│  Consumer thread(s): Any thread calling the API                        │
│  ├── SubscribeAsync, SetDataRefValueAsync, SendCommandAsync            │
│  ├── ID caches use ConcurrentDictionary (cross-thread access)          │
│  └── WebSocket sends are serialized by the runtime                     │
│                                                                        │
│  Shared state (ConcurrentDictionary, cross-thread):                    │
│  ├── _dataRefIdCache           (path → XPlaneDataRefInfo)              │
│  ├── _reverseDataRefIdCache    (id → path)                             │
│  ├── _commandIdCache           (path → id)                             │
│  └── _reverseCommandIdCache    (id → path)                             │
│                                                                        │
│  Worker-thread-only state (no cross-thread access):                    │
│  ├── _subscriptions            ((id, index) → SubscriptionCallbacks)   │
│  │   └─ Polymorphic: SubscriptionCallbacks<float>, <int>,              │
│  │      <double>, or <string> depending on dataref type                │
│  ├── _subscribedIndices        (id → SortedSet<int>)                   │
│  ├── _commandSubscriptions     (id → (commandPath, List<Action<bool>>))│
│  └── _subscriptionRegistry     (Guid → SubscriptionEntry)              │
│                                                                        │
│  Channel<XPlaneDataMessage> _dataChannel:                              │
│  ├── Bounded(100), DropOldest                                          │
│  ├── SingleReader = true (Worker reads via HandleData)                 │
│  └── Writer: ReceiveLoopAsync (single writer in practice)              │
│                                                                        │
│  int _nextReqId:                                                       │
│  └── Incremented atomically via Interlocked.Increment                  │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

### Why three tasks?

The WebSocket read must never stall. If the Worker or callbacks are slow, stalling the read would cause X-Plane's WebSocket send buffer to fill up. The bounded `_dataChannel` decouples the receive loop from the Worker. The unbounded `_callbacks` channel further decouples the Worker from consumer callbacks. If the receive loop outpaces the Worker, the oldest (most stale) messages are dropped automatically — this is acceptable because dataref updates are idempotent (only the latest value matters). The Worker itself is never blocked by slow consumer callbacks since it uses non-blocking `TryWrite` to enqueue `CallbackItem` entries.

---

## 9. WebSocket Protocol Details

All outbound messages use a common envelope:

```json
{
  "req_id": 42,
  "type": "dataref_subscribe_values",
  "params": { ... }
}
```

`req_id` is monotonically increasing, generated via `Interlocked.Increment`.

### Outbound Message Types

| Type | Params type | Purpose |
|---|---|---|
| `dataref_subscribe_values` | `DataRefSubscribeParams` | Start receiving updates |
| `dataref_unsubscribe_values` | `DataRefSubscribeParams` or `DataRefUnsubscribeAllParams` | Stop receiving updates |
| `dataref_set_values` | `DataRefSetValuesParams` | Write values to datarefs |
| `command_set_is_active` | `CommandSetActiveParams` | Activate / deactivate commands |
| `command_subscribe_is_active` | `CommandSubscribeParams` | Start receiving command status |
| `command_unsubscribe_is_active` | `CommandSubscribeParams` or `CommandUnsubscribeAllParams` | Stop receiving command status |

### Inbound Message Types

| Type | Handling | Dispatched to |
|---|---|---|
| `dataref_update_values` | `HandleDataRefUpdates` | `_subscriptions` callbacks (via `DispatchNumericUpdate` / `DispatchStringUpdate`) |
| `command_update_is_active` | `HandleCommandUpdates` | `_commandSubscriptions` callbacks |
| `result` | Deserialized as `WsResultMessage` | Logged if `!Success` |

### Fire-and-Forget Design

#### WebSocket sends

```
SendWebSocketFireAndForgetAsync<T>(request, jsonTypeInfo)
   │
   ├── Check: _webSocket.State == Open?
   │   └── No → throw InvalidOperationException
   │
   ├── Serialize to UTF-8 bytes (source-generated JsonSerializer)
   │
   └── await _webSocket.SendAsync(bytes, Text, endOfMessage: true)
       └── No result awaiting — X-Plane "result" messages are logged
           but never correlated back to requests
```

#### HTTP fire-and-forget (when `fireForgetOnHttpTransport = true`)

HTTP write methods (`SetDataRefValueByIdAsync`, `ActivateCommandAsync`,
`StartFlightAsync`, `UpdateFlightAsync`) use an async local function pattern:

```
Method(id, value, ...)
   │
   ├── Prepare request body, content, URL
   │
   ├── _fireForgetOnHttpTransport?
   │   ├── Yes:
   │   │   _ = FireAndForgetAsync();   // discard task
   │   │   return;                     // caller returns immediately
   │   │
   │   │   async Task FireAndForgetAsync()
   │   │   {
   │   │       try { using var response = await SendAsync(); }
   │   │       catch (Exception ex) { _logger.LogWarning(...); }
   │   │   }
   │   │
   │   └── No:
   │       using var response = await SendAsync();
   │       response.EnsureSuccessStatusCode();
   │
   └── Task<HttpResponseMessage> SendAsync()
       └── Single local function — shared by both paths
           to avoid code duplication
```

The local `SendAsync()` function ensures the HTTP call is written once.
The `using` declaration on `HttpResponseMessage` ensures the response
stream and underlying connection are returned to the pool promptly.

**Why fire-and-forget?** X-Plane does not reliably deliver `result` responses while the receive loop is busy dispatching subscription callbacks. Blocking on acknowledgements would introduce deadlock potential and latency with no practical benefit for real-time cockpit simulation. For HTTP, the fire-and-forget mode provides similar low-latency semantics when the caller does not need confirmation.

---

## 10. REST API Details

REST is used for two purposes:

### 10.1 ID Resolution (internal, via high-level API)

```
GET /api/v3/datarefs?filter[name]={path}&fields=id,name
GET /api/v3/commands?filter[name]={path}&fields=id,name
```

These are called lazily on first use and cached.

### 10.2 Direct REST Operations (via IXPlaneApi)

| Method | HTTP | Endpoint |
|---|---|---|
| `GetCapabilitiesAsync` | GET | `/api/capabilities` |
| `ListDataRefsAsync` | GET | `/api/v3/datarefs` |
| `GetDataRefCountAsync` | GET | `/api/v3/datarefs/count` |
| `GetDataRefValueAsync` | GET | `/api/v3/datarefs/{id}/value` |
| `SetDataRefValueByHttpAsync` | PATCH | `/api/v3/datarefs/{id}/value` |
| `ListCommandsAsync` | GET | `/api/v3/commands` |
| `GetCommandCountAsync` | GET | `/api/v3/commands/count` |
| `ActivateCommandAsync` | POST | `/api/v3/command/{id}/activate` |
| `StartFlightAsync` | POST | `/api/v3/flight` |
| `UpdateFlightAsync` | PATCH | `/api/v3/flight` |

All JSON serialization uses `XPlaneJsonContext` (source-generated `JsonSerializerContext`) with `snake_case_lower` naming policy and `WhenWritingNull` ignore condition.

---

## 11. Error Handling & Reconnection

```
ConnectWebSocketAndReceiveAsync(ct)
   │
   └── while (!ct.IsCancellationRequested)
       │
       ├── Connect WebSocket
       ├── Run ReceiveLoopAsync
       │
       ├── Server closed cleanly (CloseReceived)?
       │   └── Break loop → ConnectionClosed was already raised
       │
       ├── OperationCanceledException (ct requested)?
       │   └── Break loop (normal shutdown)
       │
       └── Any other Exception?
           │
           ├── Log: "connection lost, retrying in 3s"
           ├── Wait 3 seconds
           │
           ├── Retry connect once
           │   ├── Success → continue loop (re-enter ReceiveLoopAsync)
           │   └── Failure →
           │       ├── Log: "reconnect failed"
           │       ├── ConnectionClosed?.Invoke()
           │       └── Break loop
           │
           └── (only ONE retry attempt per disconnect)
```

**Important:** After reconnection, existing subscriptions in `_subscriptions` are **not** re-registered with X-Plane. The consumer is responsible for re-subscribing after a reconnect if needed. The `ConnectionClosed` event can be used to detect this scenario.

---

## 12. Real-World Consumer Example

The `JavaSimulator.Console` project demonstrates the library in a real cockpit hardware application. Here is the full bidirectional dataflow through the consumer:

```
┌──────────────┐     Serial     ┌────────────────────┐      Library       ┌──────────┐
│   Hardware   │ ◄────────────► │  PanelHandlerBase  │ ◄────────────────► │ X-Plane  │
│  (Arduino +  │     UART       │  └─ OvhPanelHandler│     WebSocket      │  12.1.1+ │
│   Cockpit    │                │                    │     + REST         │          │
│   Panel)     │                │                    │                    │          │
└──────────────┘                └────────────────────┘                    └──────────┘
```

### Inbound: X-Plane → Hardware (LED updates)

```
X-Plane                   Library                 OvhPanelHandler              Hardware
   │                         │                         │                          │
   │ WS: dataref_update_     │                         │                          │
   │     values              │                         │                          │
   │ { "42": 1.0 }           │                         │                          │
   │────────────────────────>│                         │                          │
   │                         │ ReceiveLoop             │                          │
   │                         │  → _dataChannel         │                          │
   │                         │  → Worker.HandleData    │                          │
   │                         │  → DispatchScalar       │                          │
   │                         │    (id=42, val=1.0)     │                          │
   │                         │  → _callbacks channel   │                          │
   │                         │    (() => cb(1.0f))     │                          │
   │                         │  → CallbackTask         │                          │
   │                         │                         │                          │
   │                         │ callback(1.0f)          │                          │
   │                         │────────────────────────>│                          │
   │                         │                         │ UpdateLed("K_U2", 1.0)   │
   │                         │                         │ → SendToHardware(        │
   │                         │                         │     "K_U2", "1")         │
   │                         │                         │ → _serialWriteQueue      │
   │                         │                         │     .Writer.TryWrite(    │
   │                         │                         │       "K_U2,1;")         │
   │                         │                         │          │               │
   │                         │                         │          ▼               │
   │                         │                         │ DrainSerialWriteQueue    │
   │                         │                         │ → SerialPort.WriteLine(  │
   │                         │                         │     "K_U2,1;")           │
   │                         │                         │────────────────────────> │
   │                         │                         │                    LED ON│
```

### Outbound: Hardware → X-Plane (button press)

```
Hardware              OvhPanelHandler               Library                    X-Plane
   │                         │                         │                          │
   │ Serial: "K02,1;"        │                         │                          │
   │────────────────────────>│                         │                          │
   │                         │ SerialPort.DataReceived │                          │
   │                         │ → ProcessReceivedData   │                          │
   │                         │ → Parse "K02" + "1"     │                          │
   │                         │ → ProcessCommandAsync(  │                          │
   │                         │     "K02", "1")         │                          │
   │                         │                         │                          │
   │                         │ HandleK02_ApuMaster(1)  │                          │
   │                         │ → _connector            │                          │
   │                         │   .SendCommandAsync(    │                          │
   │                         │     "toliss/.../Apu")   │                          │
   │                         │────────────────────────>│                          │
   │                         │                         │ ResolveCommandIdAsync    │
   │                         │                         │ → cache hit (id=100)     │
   │                         │                         │                          │
   │                         │                         │ [WebSocket transport]    │
   │                         │                         │ WS: command_set_is_      │
   │                         │                         │     active               │
   │                         │                         │ { id: 100,               │
   │                         │                         │   is_active: true,       │
   │                         │                         │   duration: 0 }          │
   │                         │                         │─────────────────────────>│
   │                         │                         │                   APU ON │
```

> **Note:** When `CommandSetDataRefTransport.Http` is selected, the command
> is sent via `POST /command/{id}/activate` with `{ "duration": 0 }` instead.
> If `fireForgetOnHttpTransport` is also enabled, the HTTP call returns immediately.

### Consumer Startup Sequence

```
Program.cs                PanelHostedService       XPlaneWebConnector      OvhPanelHandler
   │                           │                        │                       │
   │ host.RunAsync()           │                        │                       │
   │──────────────────────────>│                        │                       │
   │                           │ StartAsync()           │                       │
   │                           │                        │                       │
   │                           │ WaitUntilAvailable()   │                       │
   │                           │───────────────────────>│                       │
   │                           │                        │──── REST polls ──> X-Plane
   │                           │                        │<─── 200 OK ───────────│
   │                           │<───────────────────────│                       │
   │                           │                        │                       │
   │                           │ connector.Start()      │                       │
   │                           │───────────────────────>│                       │
   │                           │                        │──── WS connect ──> X-Plane
   │                           │                        │                       │
   │                           │ panel.ConnectAsync()   │                       │
   │                           │───────────────────────────────────────────────>│
   │                           │                        │                       │
   │                           │                        │      SerialPort.Open()│
   │                           │                        │                       │
   │                           │                        │ SubscribeToDataRefs() │
   │                           │                        │<──────────────────────│
   │                           │                        │ (50+ SubscribeAsync   │
   │                           │                        │ calls with callbacks) │
   │                           │                        │                       │
   │                           │                        │─── REST resolve IDs ─>│
   │                           │                        │─── WS subscribe ─────>│
   │                           │                        │                       │
   │                           │ "All panels init'd"    │                       │
   │                           │                        │                       │
```

---

## 13. Internal State & Data Structures

```
XPlaneWebConnector
│
├── Transport
│   ├── _httpClientFactory           IHttpClientFactory    Creates HttpClients per-call
│   ├── _httpClientName              string                Named client for IHttpClientFactory
│   ├── _webSocket                   ClientWebSocket?     Current WS connection
│   ├── _cts                         CancellationTokenSource?  Lifecycle control
│   └── _receiveTask                 Task?                Background WS receive
│
├── Caches (populated lazily, cleared on StopAsync — ConcurrentDictionary, cross-thread)
│   ├── _dataRefIdCache              ConcurrentDictionary<string, XPlaneDataRefInfo>
│                                    "sim/.../heading" → XPlaneDataRefInfo { Id=42, ... }
│   ├── _reverseDataRefIdCache       ConcurrentDictionary<long, string>
│   ├── _commandIdCache              ConcurrentDictionary<string, long>
│   │                                "sim/autopilot/heading_up" → 100
│   └── _reverseCommandIdCache       ConcurrentDictionary<long, string>
│
├── Subscriptions (Worker-thread-only — accessed exclusively by Worker + StopAsync after Worker stops)
│   ├── _subscriptions               Dictionary<(long Id, int Index), SubscriptionCallbacks>
│   │                                Polymorphic: SubscriptionCallbacks<float>, <int>,
│   │                                  <double>, or <string> depending on dataref type
│   │                                (42, -1) → SubscriptionCallbacks<float> [cb1, cb2]  // scalar
│   │                                (42,  7) → SubscriptionCallbacks<float> [cb1]      // array
│   │
│   ├── _subscribedIndices           Dictionary<long, SortedSet<int>>
│   │                                42 → { 1, 3, 7 }  // tracks which indices
│   │                                                   // are subscribed per ID
│   │
│   ├── _subscriptionRegistry        Dictionary<Guid, SubscriptionEntry>
│   │                                Per-consumer tracking. Each SubscribeAsync /
│   │                                SubscribeCommandAsync call generates a GUID.
│   │                                SubscriptionEntry contains:
│   │                                (Id, Index, Kind, Callback delegate)
│   │                                Kind: Data or Command
│   │                                Used by HandleUnsubscribeByGuidAsync for
│   │                                ref-counted unsubscription.
│   │
│   └── _commandSubscriptions        Dictionary<long,
│                                      (string commandPath, List<Action<bool>>)>
│                                    100 → ("toliss/.../Master1On", [callback1, callback2])
│
├── Message Pipeline
│   ├── _dataChannel                Channel<XPlaneDataMessage>(100, DropOldest)
│   │                                Decouples WS read from callback dispatch
│   ├── _commandChannel             Channel<CommandEnvelope<WorkerCommand, bool>>
│   │                                Subscription commands routed to worker thread
│   ├── _callbacks                  Channel<Action> (unbounded)
│   │                                Worker captures typed value in closure
│   │                                e.g. () => cb(floatValue)
│   │                                Callback task invokes Action() directly
│   └── _nextReqId                   int (Interlocked.Increment)
│
└── Configuration (immutable after construction)
├── _baseUrl                     "http://host:port/api/{apiVersion}"
├── _wsUrl                       "ws://host:port/api/{apiVersion}"
├── _capabilitiesUrl             "http://host:port/api/capabilities"
├── _transport                   CommandSetDataRefTransport (WebSocket or Http)
├── _fireForgetOnHttpTransport   bool — when true, HTTP writes return immediately
├── _httpClientName              string — named IHttpClientFactory client (default: "")
├── _readinessProbeDataRef       string? (optional plugin dataref to wait for)
└── _readinessProbeMaxRetries    int (0 = unlimited)
```

---

## 14. Design Decisions & Trade-offs

### Selectable transport (`CommandSetDataRefTransport`)

**Decision:** A single `CommandSetDataRefTransport` enum controls how both `SendCommandAsync` and `SetDataRefValueAsync` communicate with X-Plane. The choice is set once at construction time.

**Rationale:** The X-Plane API offers both WebSocket and REST for writing datarefs and activating commands. WebSocket is lower-latency (persistent connection, no HTTP overhead) but provides only fire-and-forget semantics. HTTP REST is stateless and returns immediate error feedback via HTTP status codes, which can be valuable for debugging or when a WebSocket connection is not needed for subscriptions.

**Trade-off:** A single enum controls both operations together. If an application wanted WebSocket for datarefs but HTTP for commands (or vice versa), it would need two separate connector instances or a more granular configuration. In practice, the same transport works well for both.

| | WebSocket | Http |
|---|---|---|
| Commands | WS `command_set_is_active` | POST `/command/{id}/activate` |
| Dataref writes | WS `dataref_set_values` | PATCH `/datarefs/{id}/value` |
| Latency | Lower (persistent connection) | Higher (new HTTP request per write) |
| Batching | ✅ multiple entries per frame | ❌ one request per write |
| Error feedback | Unreliable ("result" may be missed) | Immediate HTTP status codes (unless fire-and-forget) |
| Fire-and-forget | Always (by design) | Optional via `fireForgetOnHttpTransport` |
| Requires WS connection | Yes | No — fully stateless |

### Fire-and-forget sends

**Decision:** WebSocket sends never wait for a `result` response. HTTP write operations optionally run fire-and-forget when `fireForgetOnHttpTransport` is enabled.

**Rationale:** X-Plane does not reliably deliver results while processing subscription updates. Blocking would introduce deadlocks (the receive task waiting for a result that can't be delivered because the processing task is blocked waiting for the send to complete). For HTTP, fire-and-forget mode reduces latency for high-frequency writes where the caller does not need confirmation.

**Implementation:** HTTP fire-and-forget uses an async local function (`FireAndForgetAsync`) paired with a shared `SendAsync()` local function. The local function catches all exceptions and logs them as warnings. The `HttpResponseMessage` is always disposed via a `using` declaration to ensure connections are returned to the pool promptly.

**Trade-off:** Errors in outbound messages are only discovered via log output — never raised to the caller. When `fireForgetOnHttpTransport` is `false`, the HTTP response is awaited and validated with `EnsureSuccessStatusCode()`.

### HttpResponseMessage disposal

**Decision:** All `HttpResponseMessage` instances are wrapped in `using` declarations.

**Rationale:** `HttpResponseMessage` implements `IDisposable` and holds a reference to the response content stream. Without disposal, the stream stays alive until GC, delaying the return of the connection to the `HttpClient` pool. Under high-frequency writes this can exhaust the connection pool.

**Trade-off:** None — this is a pure correctness fix with no downside.

### Bounded channel with DropOldest

**Decision:** The incoming message channel has a capacity of 100 and drops the oldest message when full.

**Rationale:** Dataref updates are idempotent — only the latest value matters. If the consumer can't keep up (e.g., serial port writes at low baud rates), it's better to drop stale values than to accumulate backpressure and lag.

**Trade-off:** Under extreme load, intermediate dataref values may be lost. This is acceptable for cockpit instrumentation (displays always show the latest state) but would not be suitable for applications that need every value transition.

### Lazy ID resolution with caching

**Decision:** Dataref/command IDs are resolved via REST on first use, then cached for the session.

**Rationale:** The X-Plane API requires numeric IDs for WebSocket operations, but consumers work with human-readable paths. Eager resolution of all possible datarefs would be wasteful. Lazy resolution amortizes the cost.

**Trade-off:** The first button press or subscription for each unique dataref/command incurs a REST round-trip. During X-Plane startup (first ~60 seconds), the REST API can be slow due to simulator load. This is a known X-Plane limitation, not a library issue.

### Single callback task

**Decision:** All consumer callbacks are dispatched sequentially on a dedicated callback task, decoupled from the Worker via the `_callbacks` channel.

**Rationale:** Simplifies callback ordering guarantees and avoids concurrent callback invocations for the same dataref. The Worker is never blocked by slow callbacks — it enqueues `Action` closures (with the value captured) via non-blocking `TryWrite` and continues processing the next message immediately.

**Trade-off:** A slow callback blocks other callbacks (ordering is preserved), but does NOT block the Worker from processing incoming data or subscription commands. Consumers with heavy processing (e.g., serial port writes) can still offload to their own queue — as demonstrated by `PanelHandlerBase._serialWriteQueue` in the JavaSimulator.Console consumer.

### Source-generated JSON serialization

**Decision:** All JSON serialization uses `XPlaneJsonContext` (source-generated `JsonSerializerContext`).

**Rationale:** Avoids reflection-based serialization overhead and is AOT-compatible. The `snake_case_lower` naming policy matches X-Plane's API convention.

**Trade-off:** Adding new message types requires updating the `[JsonSerializable]` attributes on `XPlaneJsonContext`.

---

*This document reflects the library as of version 3.2.0. Last updated based on source analysis of the `nocscienceat.XPlaneWebConnector` and `JavaSimulator.Console` projects.*
