# XPlaneWebConnector.CallbackChannel.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.CallbackChannel.cs`
> **Responsibility:** Drains the `_callbacks` channel and invokes user-registered callbacks.
> **Thread context:** Dedicated callback thread (via `Task.Run`). Decouples callback execution
> from the worker thread so slow callbacks (e.g. serial port writes) never block data processing.

---

## Channel Dataflow — This File's Role

CallbackChannel.cs is the **final stage of the inbound pipeline** — the last hop
before data reaches the consumer. It reads `_callbacks` and invokes user code.

```
  X-Plane ─► Transport.cs ─► _dataChannel ─► Worker.cs ─► _callbacks ─► CallbackChannel.cs ─► Consumer
                                                              ▲               │
                                                              │               │
                                                         this channel    this file
                                                         is the only     is the only
                                                         producer:       consumer:
                                                         Worker.cs       reads + invokes
```

### End-to-end inbound dataflow (X-Plane → Consumer)

```
  X-Plane WS message
       │
       ▼
  Transport.cs:  ReceiveLoopAsync
       │         _dataChannel.Writer.TryWrite(XPlaneDataMessage)
       ▼
  ─── _dataChannel ───────────────────────────────────────────
       │
       ▼
  Worker.cs:     ProcessIncomingMessage → Dispatch*
       │         _callbacks.Writer.TryWrite(CallbackItem)
       ▼
  ─── _callbacks ─────────────────────────────────────────────
       │
       ▼
  CallbackChannel.cs:  ProcessCallbackChannelAsync
       │               switch on CallbackItem type
       ▼
  Consumer callback:   Action<SimDataRef>, Action<SimStringDataRef>, Action<long,bool>
       │
       ▼
  Panel / application code (e.g. serial port write, UI update)
```

### Channel interactions in this file

| Channel | Direction | Method |
|---|---|---|
| `_callbacks` | **reads** ← | `ProcessCallbackChannelAsync` — drains all callback items |

---

## Call Path

```
StartCallbacksAsync(ct)                              ── called from Start() in XPlaneWebConnector.cs
  └─ Task.Run →
       ProcessCallbackChannelAsync(ct)
         │
         └─ await foreach (_callbacks.Reader.ReadAllAsync)
              │
              ├─ CallbackItem.SimDataRefCb
              │    └─ cb.Callback(cb.Element)          ── user callback (e.g. panel update)
              │
              ├─ CallbackItem.SimStringDataRefCb
              │    └─ cb.Callback(cb.Element)          ── user callback
              │
              └─ CallbackItem.CommandCb
                   └─ cb.Callback(cb.Id, cb.IsActive)  ── user callback
```

---

## Methods in This File

| # | Method | Role |
|---|--------|------|
| 1 | `StartCallbacksAsync` | Launches the callback processing task |
| 2 | `ProcessCallbackChannelAsync` | Reads `_callbacks` channel and dispatches to user callbacks |

---

## Cross-File Transitions

| Direction | Target File | Mechanism | Purpose |
|---|---|---|---|
| **inbound** | ← XPlaneWebConnector.cs | `Start()` calls `StartCallbacksAsync` | Starts callback thread |
| **inbound** | ← Worker.cs | `_callbacks.Writer.TryWrite(...)` | Worker queues callback items |
| **outbound** | → User code (panels) | `cb.Callback(...)` | Invokes registered subscription callbacks |
