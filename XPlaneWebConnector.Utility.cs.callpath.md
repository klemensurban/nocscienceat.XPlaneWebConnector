# XPlaneWebConnector.Utility.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.Utility.cs`
> **Responsibility:** Pure utility methods — dataRef path parsing and byte-array decoding.
> **Thread context:** Static/pure methods — safe to call from any thread.

---

## Channel Dataflow — This File's Role

Utility.cs has **no direct channel interaction**. It provides pure helper functions
used by both sides of the channel pipeline:

```
  Consumer thread                              Worker thread
       │                                            │
       ├─ SubscribeAsync()                          ├─ DispatchArrayUpdate()
       │   └─ ParseDataRefPath()  ◄── Utility.cs    │   └─ DecodeByteArrayToString()  ◄── Utility.cs
       │                                            │
       ├─ SetDataRefValueAsync()                    │
       │   └─ ParseDataRefPath()  ◄── Utility.cs    │
       ▼                                            ▼
  _commandChannel                              _callbacks
```

---

## Call Paths

```
ParseDataRefPath(path)                               ── static, pure
  └─ ArrayIndexRegex().Match(path)                   ── source-generated regex
       returns (basePath, index) or (path, -1)

DecodeByteArrayToString(arrayElement)                ── static, pure
  └─ enumerates JSON array → byte[] → UTF-8 string
       trims trailing null bytes
```

---

## Methods in This File

| # | Method | Role | Thread |
|---|--------|------|--------|
| 1 | `ArrayIndexRegex()` | Source-generated regex for `"Foo[7]"` patterns | Any (static) |
| 2 | `ParseDataRefPath(path)` | Splits `"AirbusFBW/Foo[7]"` → `("AirbusFBW/Foo", 7)` | Any (static) |
| 3 | `DecodeByteArrayToString(arrayElement)` | JSON byte array → UTF-8 string | Worker thread |

---

## Cross-File Transitions

| Direction | Source File | Method Called | Purpose |
|---|---|---|---|
| **inbound** | ← XPlaneWebConnector.cs | `ParseDataRefPath` | `SubscribeAsync`, `SetDataRefValueAsync` |
| **inbound** | ← Worker.cs | `DecodeByteArrayToString` | `DispatchArrayUpdate` (string dataRef decoding) |
