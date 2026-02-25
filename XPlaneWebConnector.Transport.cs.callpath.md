# XPlaneWebConnector.Transport.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.Transport.cs`
> **Responsibility:** WebSocket connection management, receive loop, and send helper.
> **Thread context:** Receive loop runs on a dedicated task (thread-pool thread). Send helper is called from multiple threads.

---

## Channel Dataflow — This File's Role

Transport.cs is the **bridge between the raw WebSocket and the channel architecture**.
It is the sole writer to `_dataChannel` (inbound) and provides the send helper
that all outbound WS messages flow through.

```
                            X-Plane WebSocket
                           ▲               │
               outbound    │               │  inbound
               (WS send)   │               │  (WS receive)
                           │               ▼
               ┌───────────┴────────────────────────────────────┐
               │         Transport.cs                           │
               │                                                │
               │  SendWebSocketFireAndForget   ReceiveLoopAsync │
               │         ▲                        │             │
               └─────────┼────────────────────────┼─────────────┘
                         │                        │
          called from    │                        │ _dataChannel.Writer.TryWrite
          Worker.cs      │                        │
          + main .cs     │                        ▼
                         │               ┌──────────────┐
                         │               │ _dataChannel │
                         │               └──────┬───────┘
                         │                      │
                         │                      ▼
                         │               Worker.cs reads
                         │               via HandleData
                         │
           ┌─────────────┴──────────────────────────┐
           │  Worker.cs (subscribe/unsubscribe WS)  │
           │  XPlaneWebConnector.cs (set values WS) │
           └────────────────────────────────────────┘
```

### Channel interactions in this file

| Channel | Direction | Method |
|---|---|---|
| `_dataChannel` | **writes** → | `ReceiveLoopAsync` — enqueues every received WS message |

---

## Connection & Receive Loop

```
ConnectWebSocketAndReceiveAsync(ct)                  ── called from Start() in XPlaneWebConnector.cs
  │
  └─ while (!ct.IsCancellationRequested)
       ├─ _webSocket.ConnectAsync(wsUrl)
       ├─ ReceiveLoopAsync(ct)
       │    │
       │    └─ while (WebSocket is Open)
       │         ├─ _webSocket.ReceiveAsync(buffer)
       │         ├─ Close message?
       │         │    └─ ConnectionClosed?.Invoke()              ──► event in XPlaneWebConnector.cs
       │         ├─ Single frame (fast path):
       │         │    └─ GC.AllocateUninitializedArray + copy
       │         ├─ Multi frame (slow path):
       │         │    └─ AssembleMultiFrameMessageAsync()        ── this file
       │         │         └─ ArrayPool rent/grow/return pattern
       │         └─ _dataChannel.Writer.TryWrite(dataMessage)   ──► Worker.cs picks up via HandleData
       │
       ├─ CloseReceived? → break (graceful shutdown)
       └─ Exception? → retry once after 3s
            ├─ success → continue loop
            └─ failure → ConnectionClosed?.Invoke() + break
```

---

## Send Helper

```
SendWebSocketFireAndForgetAsync<T>(request, typeInfo)
  ├─ guard: WebSocket must be Open
  ├─ JsonSerializer.SerializeToUtf8Bytes(request)
  └─ _webSocket.SendAsync(bytes)
```

### Callers of SendWebSocketFireAndForgetAsync

| Source File | Calling Method |
|---|---|
| **XPlaneWebConnector.cs** | `SetDataRefValuesByWsAsync` |
| **XPlaneWebConnector.cs** | `UnsubscribeDataRefsAsync` |
| **XPlaneWebConnector.cs** | `SubscribeCommandUpdatesAsync` |
| **XPlaneWebConnector.cs** | `UnsubscribeCommandUpdatesAsync` |
| **XPlaneWebConnector.cs** | `SetCommandActiveAsync` |
| **Worker.cs** | `SubscribeDataRefsAsync` |
| **Worker.cs** | `UnsubscribeAllDataRefsAsync` |
| **Worker.cs** | `UnsubscribeAllCommandUpdatesAsync` |
| **Worker.cs** | `HandleWorkerCommandAsync` (SubscribeCommandUpdates case, inline) |

---

## Methods in This File

| # | Method | Role |
|---|--------|------|
| 1 | `ConnectWebSocketAndReceiveAsync` | Connection lifecycle with reconnect logic |
| 2 | `ReceiveLoopAsync` | Fast read loop — enqueues raw bytes to `_dataChannel` |
| 3 | `AssembleMultiFrameMessageAsync` | Pooled multi-frame message assembly |
| 4 | `SendWebSocketFireAndForgetAsync<T>` | Serializes and sends WS message (no ack) |

---

## Cross-File Transitions

| Direction | Target File | Mechanism | Purpose |
|---|---|---|---|
| **inbound** | ← XPlaneWebConnector.cs | `Start()` calls `ConnectWebSocketAndReceiveAsync` | Starts receive loop |
| **inbound** | ← Worker.cs | calls `SendWebSocketFireAndForgetAsync` | Worker sends subscribe/unsubscribe |
| **inbound** | ← XPlaneWebConnector.cs | calls `SendWebSocketFireAndForgetAsync` | API methods send WS messages |
| **outbound** | → Worker.cs | `_dataChannel.Writer.TryWrite` | Raw WS messages queued for worker thread |
| **outbound** | → XPlaneWebConnector.cs | `ConnectionClosed?.Invoke()` | Signals connection loss |
