# XPlaneWebConnector.CallbackChannel.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.CallbackChannel.cs`
> **Responsibility:** Drains the `_callbacks` channel and invokes user-registered callbacks.
> **Thread context:** Dedicated callback thread (via `Task.Run`). Decouples callback execution
> from the worker thread so slow callbacks (e.g. serial port writes) never block data processing.

---

## Channel Dataflow — This File's Role

CallbackChannel.cs is the **final stage of the inbound pipeline** — the last hop
before data reaches the consumer. It reads `_callbacks` (`Channel<Action>`) and invokes
each `Action` closure, which in turn calls the consumer's typed callback with the captured value.

```
  X-Plane ─► Transport.cs ─► _dataChannel ─► Worker.cs ─► _callbacks ─► CallbackChannel.cs ─► Consumer
                                                          ▲               │
                                                          │               │
                                                     this channel    this file
                                                     is the only     is the only
                                                     producer:       consumer:
                                                     Worker.cs       reads + invokes
                                                     (captures       Action()
                                                      value in
                                                      closure)
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
       │         _callbacks.Writer.TryWrite(() => cb(typedValue))
       ▼
  ─── _callbacks (Channel<Action>) ─────────────────────────────
       │
       ▼
  CallbackChannel.cs:  ProcessCallbackChannelAsync
       │               callback()   ──>  invokes the closure
       ▼
   Consumer callback:   Action<float>, Action<int>, Action<double>, or Action<string>
       │                (typed value captured in the closure by the Worker)
       ▼
  Panel / application code (e.g. serial port write, UI update)
```

### Channel interactions in this file

| Channel | Direction | Method |
|---|---|---|
| `_callbacks` (`Channel<Action>`) | **reads** ← | `ProcessCallbackChannelAsync` — drains all Action closures and invokes them |

---

## Call Path

```
StartCallbacksAsync(ct)                              ── called from Start() in XPlaneWebConnector.cs
  └─ Task.Run →
       ProcessCallbackChannelAsync(ct)
         │
         └─ await foreach (Action callback in _callbacks.Reader.ReadAllAsync)
              │
              └─ callback()                           ── invokes the closure captured by the Worker
                  │                                      e.g. () => userCallback(floatValue)
                  └─ user callback executes              (panel update, serial write, etc.)
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
