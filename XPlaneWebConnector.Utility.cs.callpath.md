# XPlaneWebConnector.Utility.cs — Call Path Diagram

> **Partial class file:** `XPlaneWebConnector.Utility.cs`
> **Responsibility:** Pure utility methods — dataRef path parsing.
> **Thread context:** Static/pure methods — safe to call from any thread.

---

## Channel Dataflow — This File's Role

Utility.cs has **no direct channel interaction**. It provides a pure helper function
used by the consumer-thread side of the pipeline:

```
  Consumer thread
       │
       ├─ SubscribeAsync()
       │   └─ ParseDataRefPath()  ◄── Utility.cs
       │
       ├─ SetDataRefValueAsync()
       │   └─ ParseDataRefPath()  ◄── Utility.cs
       ▼
  _commandChannel
```

---

## Call Paths

```
ParseDataRefPath(path)                               ── static, pure
  └─ ArrayIndexRegex().Match(path)                   ── source-generated regex
       returns (basePath, index) or (path, -1)
```

---

## Methods in This File

| # | Method | Role | Thread |
|---|--------|------|--------|
| 1 | `ArrayIndexRegex()` | Source-generated regex for `"Foo[7]"` patterns | Any (static) |
| 2 | `ParseDataRefPath(path)` | Splits `"AirbusFBW/Foo[7]"` → `("AirbusFBW/Foo", 7)` | Any (static) |

---

## Cross-File Transitions

| Direction | Source File | Method Called | Purpose |
|---|---|---|---|
| **inbound** | ← XPlaneWebConnector.cs | `ParseDataRefPath` | `SubscribeAsync`, `SetDataRefValueAsync` (via `ResolveAndValidateDataRefAsync`) |
