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

`nocscienceat.XPlaneWebConnector` is a .NET 10 library that communicates with X-Plane 12.1.1+ via its built-in REST (`/api/v3`) and WebSocket (`ws://host:port/api/v3`) APIs. It wraps the low-level HTTP and WebSocket protocol handling into a clean, async interface for .NET applications.

The library provides two abstraction levels:

- **High-level** (`IXPlaneWebConnector`): Subscribe to datarefs with callbacks, set values by path, send commands by name. The library handles name→ID resolution, WebSocket framing, and JSON serialization transparently.
- **Low-level** (`IXPlaneApi`): Direct access to the X-Plane REST endpoints and raw WebSocket operations using session IDs.

A third interface (`IXPlaneAvailabilityCheck`) handles startup sequencing — waiting for X-Plane to be reachable and for plugin datarefs to be registered.

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
│                │   IXPlaneAvailability   │ Startup sequencing       │
│                │        Check            │                          │
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
│  /api/v3/datarefs    /api/v3/commands    ws://host:port/api/v3      │
│  /api/v3/flight      /api/capabilities                              │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 2. Interface Layering

The three interfaces are implemented by a single class (`XPlaneWebConnector`) but serve different roles:

```
                    ┌───────────────────────────────┐
                    │      XPlaneWebConnector       │
                    │   (sealed partial class)      │
                    ├───────────────────────────────┤
                    │ implements:                   │
                    │  • IXPlaneWebConnector        │
                    │  • IXPlaneApi                 │
                    │  • IXPlaneAvailabilityCheck   │
                    │  • IDisposable                │
                    └───────────────────────────────┘

 ┌─────────────────────────┐  ┌──────────────────────────┐  ┌──────────────────────────┐
 │  IXPlaneWebConnector    │  │   IXPlaneApi             │  │ IXPlaneAvailabilityCheck │
 │  (high-level)           │  │   (low-level)            │  │ (startup)                │
 ├─────────────────────────┤  ├──────────────────────────┤  ├──────────────────────────┤
 │ Start()                 │  │ GetCapabilitiesAsync()   │  │ IsAvailableAsync()       │
 │ StopAsync()             │  │ ListDataRefsAsync()      │  │ WaitUntilAvailableAsync()│
 │ SubscribeAsync()        │  │ GetDataRefCountAsync()   │  │ ConnectionClosed event   │
 │ SetDataRefValueAsync()  │  │ GetDataRefValueAsync()   │  └──────────────────────────┘
 │ SendCommandAsync()      │  │ SetDataRefValueByIdAs()  │
 │ SendCommandAsync(,dur)  │  │ SetDataRefValuesByWs()   │
 │ Dispose()               │  │ ListCommandsAsync()      │
 └─────────────────────────┘  │ GetCommandCountAsync()   │
                              │ ActivateCommandAsync()   │
                              │ StartFlightAsync()       │
                              │ UpdateFlightAsync()      │
                              │ SubscribeDataRefsAsync() │
                              │ UnsubscribeDataRefsAs()  │
                              │ UnsubscribeAllDataRefs() │
                              │ Subscribe/Unsub CmdUpd() │
                              │ SetCommandActiveAsync()  │
                              └──────────────────────────┘
```

### When to use which interface

| Use case | Interface | Example |
|---|---|---|
| Subscribe to a dataref with a callback | `IXPlaneWebConnector` | Panel LED updates |
| Set a switch position by dataref path | `IXPlaneWebConnector` | Toggle landing lights |
| Send a one-shot command by name | `IXPlaneWebConnector` | APU master on |
| Query all registered datarefs | `IXPlaneApi` | Tooling / debugging |
| Batch-set multiple datarefs in one WS frame | `IXPlaneApi` | High-frequency syncing |
| Control command begin/end precisely | `IXPlaneApi` | Long-press fire test |
| Wait for X-Plane to start up | `IXPlaneAvailabilityCheck` | Hosted service startup |
| React to X-Plane shutdown | `IXPlaneAvailabilityCheck` | Graceful app exit |

---

## 3. Type System

The library uses three primary types for consumer-facing dataref and command references:

```
  SimDataRefBase (abstract)
       │
       ├── SimDataRef          DataRef: string, Value: float
       │                       Numeric datarefs (most common)
       │
       └── SimStringDataRef    DataRef: string, Value: string
                               String/data-type datarefs (tail number, etc.)

  SimCommand                   Command: string, Description: string
                               Command reference (e.g. "sim/autopilot/heading_up")
```

**Key property: `DataRef` path format**

The `DataRef` path supports array indexing via bracket notation. The library parses this internally:

```
"sim/cockpit/autopilot/heading"        → basePath = "sim/.../heading",   index = -1 (scalar)
"AirbusFBW/Foo[7]"                     → basePath = "AirbusFBW/Foo",     index = 7  (array)
"sim/cockpit2/engine/indicators/N1[0]" → basePath = "sim/.../N1",        index = 0  (array)
```

The `ParseDataRefPath` method uses a source-generated regex to extract base path and index.

---

## 4. Lifecycle & Connection Management

### Startup Sequence

```
Consumer                        Library                          X-Plane
   │                               │                               │
   │  new XPlaneWebConnector(...)  │                               │
   │──────────────────────────────>│  (constructor: saves config,  │
   │                               │   creates HttpClient)         │
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
   │                               │    └─ ProcessIncomingMessages │
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
   │──────────────────────────────>│  Dispose CTS, WebSocket,      │
   │                               │  HttpClient                   │
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

There are three outbound paths, each with a different protocol and purpose.

### 5.1 Subscribe to Dataref (High-Level)

```
Consumer                         Library                                      X-Plane
   │                                │                                            │
   │ SubscribeAsync(SimDataRef, cb) │                                            │
   │───────────────────────────────>│                                            │
   │                                │                                            │
   │                                │  ParseDataRefPath("AirbusFBW/Foo[7]")      │
   │                                │  → basePath="AirbusFBW/Foo", index=7       │
   │                                │                                            │
   │                                │  ResolveDataRefIdAsync("AirbusFBW/Foo")    │
   │                                │  ├─ cache hit? → return id                 │
   │                                │  └─ cache miss:                            │
   │                                │     GET /api/v3/datarefs                   │
   │                                │       ?filter[name]=AirbusFBW/Foo          │
   │                                │       &fields=id,name ────────────────────>│
   │                                │     ← { data: [{ id: 42 }] } ──────────────│
   │                                │     cache[path] = 42                       │
   │                                │                                            │
   │                                │  _subscriptions[(42, 7)] = (dataref, cb)   │
   │                                │  _subscribedIndices[42].Add(7)             │
   │                                │                                            │
   │                                │  WS → { req_id: N,                         │
   │                                │         type: "dataref_subscribe_values",  │
   │                                │         params: { datarefs: [              │
   │                                │           { id: 42, index: 7 }             │
   │                                │         ]}} ──────────────────────────────>│
   │                                │                                            │
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
   │                                        │  ├─ [fireForget=false]          │
   │                                        │  │  PATCH /datarefs/42/value   │
   │                                        │  │    ?index=3                 │
   │                                        │  │  ───────────────────────>   │
   │                                        │  │  ←── 200 OK ───────────────│
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
The `SendCommandAsync(command)` overload sends with `duration = 0` (press & release).
The `SendCommandAsync(command, duration)` overload allows holding the command for 0–10 seconds.

#### Transport: WebSocket (default)

```
Consumer                                Library                          X-Plane
   │                                       │                                │
   │ SendCommandAsync(SimCommand)          │                                │
   │──────────────────────────────────────>│                                │
   │                                       │  ResolveCommandIdAsync(name)   │
   │                                       │  → id (from cache or REST)     │
   │                                       │                                │
   │                                       │  WS → { req_id: N,             │
   │                                       │         type: "command_set_    │
   │                                       │               is_active",      │
   │                                       │         params: { commands: [  │
   │                                       │           { id: 100,           │
   │                                       │             is_active: true,   │
   │                                       │             duration: 0 }      │
   │                                       │         ]}} ──────────────────>│
   │                                       │                                │
```

#### Transport: Http

```
Consumer                                Library                          X-Plane
   │                                       │                                │
   │ SendCommandAsync(SimCommand, 0.5f)    │                                │
   │──────────────────────────────────────>│                                │
   │                                       │  ResolveCommandIdAsync(name)   │
   │                                       │  → id (from cache or REST)     │
   │                                       │                                │
   │                                       │  ActivateCommandAsync()        │
   │                                       │  ├─ [fireForget=false]          │
   │                                       │  │  POST /command/100/activate │
   │                                       │  │  Body: { "duration": 0.5 }   │
   │                                       │  │  ──────────────────────>    │
   │                                       │  │  ←── 200 OK ──────────────│
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
  │    (transport selected by CommandSetDataRefTransport)                    │
  ├─────────────────────────────────────────────────────────────────────────┤
  │                                                                         │
  │ Consumer ──> IXPlaneWebConnector                                        │
  │              │                                                          │
  │              ├── SubscribeAsync()                                       │
  │              │     ├─ ResolveDataRefIdAsync() ──── REST GET ──> X-Plane │
  │              │     └─ SendDataRefSubscribeAsync() ── WS ──────> X-Plane │
  │              │                                                          │
  │              ├── SetDataRefValueAsync()                                 │
  │              │     ├─ ResolveDataRefIdAsync() ──── REST GET ──> X-Plane │
  │              │     ├─ [WebSocket]  SetDataRefValuesByWsAsync() ─ WS ──> │
  │              │     └─ [Http]       SetDataRefValueByIdAsync()           │
  │              │                      └─ PATCH /datarefs/{id}/value ──>  │
  │              │                                                          │
  │              └── SendCommandAsync()                                     │
  │                    ├─ ResolveCommandIdAsync() ──── REST GET ──> X-Plane │
  │                    ├─ [WebSocket]  SetCommandActiveAsync() ── WS ────>  │
  │                    └─ [Http]       ActivateCommandAsync()               │
  │                                    └─ POST /command/{id}/activate ──>  │
  │                                                                         │
  │ Consumer ──> IXPlaneApi                                                 │
  │              │                                                          │
  │              ├── ListDataRefsAsync() ─────────── REST GET ────> X-Plane │
  │              ├── SetDataRefValueByIdAsync() ──── REST PATCH ──> X-Plane │
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

Inbound data arrives exclusively via WebSocket. The library uses a **two-stage pipeline** to decouple fast WebSocket reads from potentially slow callback processing:

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
   │                          │  │ Channel<byte[]> (capacity: 50)   │      │
   │                          │  │ BoundedChannelFullMode.DropOldest│      │
   │                          │  │ SingleReader = true              │      │
   │                          │  └──────────────────────────────────┘      │
   │                          │                 │                          │
   │                          │                 ▼                          │
   │                          │  ┌─────────────────────────────────────┐   │
   │                          │  │ Stage 2: ProcessIncomingMessagesAs  │   │
   │                          │  │ (Thread: processing task)           │   │
   │                          │  │                                     │   │
   │                          │  │ • JSON parse (JsonDocument)         │   │
   │                          │  │ • Dispatch by message type          │   │
   │                          │  │ • Look up registered callbacks      │   │
   │                          │  │ • Invoke consumer callbacks         │   │
   │                          │  └─────────────────────────────────────┘   │
   │                          │                        │                   │
   │                          │                        ▼                   │
   │                          │            callback(SimDataRef, float) ───>│
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
   │       ├── data[id] is Number (scalar)
   │       │   └── DispatchScalarUpdate(id, float)
   │       │       └── _subscriptions[(id, -1)] → callback(SimDataRef, value)
   │       │
   │       ├── data[id] is Array
   │       │   └── DispatchArrayUpdate(id, JsonElement)
   │       │       │
   │       │       ├── _stringSubscriptions[(id, -1)] exists?
   │       │       │   └── DecodeBase64ArrayToString() → callback(SimStringDataRef, string)
   │       │       │
   │       │       ├── _subscribedIndices[id] exists?
   │       │       │   └── For each position in sorted index set:
   │       │       │       └── _subscriptions[(id, idx)] → callback(SimDataRef, value)
   │       │       │
   │       │       └── No indices tracked:
   │       │           └── Iterate array positions 0..N:
   │       │               └── _subscriptions[(id, pos)] → callback(SimDataRef, value)
   │       │
   │       └── data[id] is String (base64-encoded)
   │           └── DispatchStringUpdate(id, base64)
   │               └── Base64 decode → _stringSubscriptions[(id, -1)] → callback
   │
   ├── type == "command_update_is_active"
   │   └── HandleCommandUpdates()
   │       └── For each command ID in data:
   │           └── _commandSubscriptions[id] → callback(id, bool isActive)
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

String datarefs from X-Plane can arrive in two formats:

```
Format 1: Base64 string
  { "42": "SEVMTE8=" }
  → Base64 decode → "HELLO"

Format 2: Byte array (data-type datarefs)
  { "42": [72, 69, 76, 76, 79, 0, 0, 0] }
  → DecodeBase64ArrayToString()
  → Assemble bytes, trim trailing nulls → "HELLO"
```

---

## 7. ID Resolution & Caching

X-Plane's WebSocket API uses numeric session IDs, not string paths. The library resolves names to IDs via REST and caches them:

```
┌─────────────────────────────────────────────────────────────────────┐
│                       ID Resolution Flow                            │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Consumer calls:                                                    │
│    SubscribeAsync(new SimDataRef { DataRef = "sim/.../heading" })   │
│                                                                     │
│         │                                                           │
│         ▼                                                           │
│  ┌──────────────────────┐                                           │
│  │  _dataRefIdCache     │                                           │
│  │  ConcurrentDictionary│     ┌─── hit ──> return cached id         │
│  │  <string, long>      │─────┤                                     │
│  └──────────────────────┘     └─── miss ──────┐                     │
│                                               │                     │
│                                               ▼                     │
│                                  ┌─────────────────────────┐        │
│                                  │ REST: GET /api/v3/      │        │
│                                  │  datarefs?filter[name]= │        │
│                                  │  {path}&fields=id,name  │        │
│                                  └─────────────────────────┘        │
│                                               │                     │
│                                               ▼                     │
│                                  ┌──────────────────────────┐       │
│                                  │ Parse response:          │       │
│                                  │ { data: [{ id: 42 }] }   │       │
│                                  │                          │       │
│                                  │ _dataRefIdCache[path]=42 │       │
│                                  │ return 42                │       │
│                                  └──────────────────────────┘       │
│                                                                     │
│  Same flow for _commandIdCache via ResolveCommandIdAsync()          │
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
┌─────────────────────────────────────────────────────────────────────┐
│                       Thread / Task Map                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Task 1: ConnectWebSocketAndReceiveAsync  (background, long-lived)  │
│  ├── Owns the ClientWebSocket lifecycle                             │
│  ├── Runs ReceiveLoopAsync in a tight loop                          │
│  ├── Handles reconnection on failure                                │
│  └── Writes raw byte[] into _incomingMessages Channel               │
│                                                                     │
│  Task 2: ProcessIncomingMessagesAsync  (background, long-lived)     │
│  ├── Reads from _incomingMessages Channel                           │
│  ├── Parses JSON and dispatches callbacks                           │
│  └── Consumer callbacks run ON THIS TASK                            │
│      → A slow callback blocks all other dispatches                  │
│      → Consumers should offload heavy work (e.g. to a Channel)      │
│                                                                     │
│  Consumer thread(s): Any thread calling the API                     │
│  ├── SubscribeAsync, SetDataRefValueAsync, SendCommandAsync         │
│  ├── These acquire no locks (ConcurrentDictionary is lock-free)     │
│  └── WebSocket sends are serialized by the runtime                  │
│                                                                     │
│  Shared state (all ConcurrentDictionary, thread-safe):              │
│  ├── _dataRefIdCache           (path → id)                          │
│  ├── _commandIdCache           (path → id)                          │
│  ├── _subscriptions            ((id, index) → callback)             │
│  ├── _stringSubscriptions      ((id, index) → callback)             │
│  ├── _subscribedIndices        (id → SortedSet<int>)                │
│  │   └── SortedSet access is protected by lock(indices)             │
│  └── _commandSubscriptions     (id → callback)                      │
│                                                                     │
│  Channel<byte[]> _incomingMessages:                                 │
│  ├── Bounded(50), DropOldest                                        │
│  ├── SingleReader = true (only ProcessIncomingMessagesAsync reads)  │
│  └── Writer: ReceiveLoopAsync (single writer in practice)           │
│                                                                     │
│  int _nextReqId:                                                    │
│  └── Incremented atomically via Interlocked.Increment               │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Why two tasks?

The WebSocket read must never stall. If callbacks are slow (e.g., a consumer writes to a serial port at 9600 baud), stalling the read would cause X-Plane's WebSocket send buffer to fill up. The bounded `Channel<byte[]>` decouples the two. If the processing task falls behind, the oldest (most stale) messages are dropped automatically — this is acceptable because dataref updates are idempotent (only the latest value matters).

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
| `dataref_update_values` | `HandleDataRefUpdates` | `_subscriptions` / `_stringSubscriptions` callbacks |
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
| `SetDataRefValueByIdAsync` | PATCH | `/api/v3/datarefs/{id}/value` |
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
   │                         │  → Channel<byte[]>      │                          │
   │                         │  → ProcessIncoming      │                          │
   │                         │  → DispatchScalar       │                          │
   │                         │    (id=42, val=1.0)     │                          │
   │                         │                         │                          │
   │                         │ callback(SimDataRef,    │                          │
   │                         │          1.0)           │                          │
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
   │                         │     GetCommand(         │                          │
   │                         │       "ApuMaster"))     │                          │
   │                         │────────────────────────>│                          │
   │                         │                         │ ResolveCommandIdAsync    │
   │                         │                         │ → cache hit (id=100)     │
   │                         │                         │                          │
   │                         │                         │ [WebSocket transport]     │
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
│   ├── _httpClient                  HttpClient           Shared for all REST calls
│   ├── _webSocket                   ClientWebSocket?     Current WS connection
│   ├── _cts                         CancellationTokenSource?  Lifecycle control
│   └── _receiveTask                 Task?                Background WS receive
│
├── Caches (populated lazily, cleared on StopAsync)
│   ├── _dataRefIdCache              ConcurrentDictionary<string, long>
│   │                                "sim/.../heading" → 42
│   └── _commandIdCache              ConcurrentDictionary<string, long>
│                                    "sim/autopilot/heading_up" → 100
│
├── Subscriptions (populated by SubscribeAsync, cleared on StopAsync)
│   ├── _subscriptions               ConcurrentDictionary<(long Id, int Index),
│   │                                  (SimDataRef, Action<SimDataRef, float>)>
│   │                                (42, -1) → (element, callback)   // scalar
│   │                                (42,  7) → (element, callback)   // array index
│   │
│   ├── _stringSubscriptions         ConcurrentDictionary<(long Id, int Index),
│   │                                  (SimStringDataRef, Action<SimStringDataRef, string>)>
│   │
│   ├── _subscribedIndices           ConcurrentDictionary<long, SortedSet<int>>
│   │                                42 → { 1, 3, 7 }  // tracks which indices
│   │                                                   // are subscribed per ID
│   │
│   └── _commandSubscriptions        ConcurrentDictionary<long, Action<long, bool>>
│                                    100 → callback(id, isActive)
│
├── Message Pipeline
│   ├── _incomingMessages            Channel<byte[]>(50, DropOldest)
│   │                                Decouples WS read from callback dispatch
│   └── _nextReqId                   int (Interlocked.Increment)
│
└── Configuration (immutable after construction)
├── _baseUrl                     "http://host:port/api/v3"
├── _wsUrl                       "ws://host:port/api/v3"
├── _capabilitiesUrl             "http://host:port/api/capabilities"
├── _transport                   CommandSetDataRefTransport (WebSocket or Http)
├── _fireForgetOnHttpTransport   bool — when true, HTTP writes return immediately
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

**Decision:** The incoming message channel has a capacity of 50 and drops the oldest message when full.

**Rationale:** Dataref updates are idempotent — only the latest value matters. If the consumer can't keep up (e.g., serial port writes at low baud rates), it's better to drop stale values than to accumulate backpressure and lag.

**Trade-off:** Under extreme load, intermediate dataref values may be lost. This is acceptable for cockpit instrumentation (displays always show the latest state) but would not be suitable for applications that need every value transition.

### Lazy ID resolution with caching

**Decision:** Dataref/command IDs are resolved via REST on first use, then cached for the session.

**Rationale:** The X-Plane API requires numeric IDs for WebSocket operations, but consumers work with human-readable paths. Eager resolution of all possible datarefs would be wasteful. Lazy resolution amortizes the cost.

**Trade-off:** The first button press or subscription for each unique dataref/command incurs a REST round-trip. During X-Plane startup (first ~60 seconds), the REST API can be slow due to simulator load. This is a known X-Plane limitation, not a library issue.

### Single processing task

**Decision:** All consumer callbacks are dispatched sequentially on a single processing task.

**Rationale:** Simplifies callback ordering guarantees and avoids concurrent callback invocations for the same dataref.

**Trade-off:** A slow callback blocks all other dispatches. Consumers with heavy processing (e.g., serial port writes) should offload work to their own queue/channel — as demonstrated by `PanelHandlerBase._serialWriteQueue` in the JavaSimulator.Console consumer.

### Source-generated JSON serialization

**Decision:** All JSON serialization uses `XPlaneJsonContext` (source-generated `JsonSerializerContext`).

**Rationale:** Avoids reflection-based serialization overhead and is AOT-compatible. The `snake_case_lower` naming policy matches X-Plane's API convention.

**Trade-off:** Adding new message types requires updating the `[JsonSerializable]` attributes on `XPlaneJsonContext`.

---

*This document reflects the library as of version 1.1.0. Last updated based on source analysis of the `nocscienceat.XPlaneWebConnector` and `JavaSimulator.Console` projects.*
