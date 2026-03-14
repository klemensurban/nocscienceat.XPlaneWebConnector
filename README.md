# nocscienceat.XPlaneWebConnector

A .NET library for communicating with **X-Plane 12.1.1+** via the built-in REST and WebSocket API.

Provides a high-level interface for subscribing to datarefs, setting dataref values, and sending commands — as well as full low-level access to the X-Plane web API surface.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

> **⚠️ Breaking changes in v3.x** — see [Breaking Changes](#breaking-changes-from-2x) below.

## Features

- **Typed WebSocket dataref subscriptions** — subscribe with `Action<float>`, `Action<int>`, `Action<double>`, or `Action<string>` callbacks matching the X-Plane dataref type; returns `IDisposable` for per-consumer unsubscription
- **WebSocket command activation subscriptions** — subscribe to command activation status updates with the same `IDisposable` pattern as datarefs
- **Disposable subscription handles** — multiple consumers can subscribe to the same dataref or command; disposing a handle removes only that consumer's callback; X-Plane is unsubscribed only when the last consumer disposes
- **Dataref writes** — set `float`, `int`, `double`, and `string` datarefs by path
- **Command execution** — send one-shot commands by path or control begin/end activation, with optional hold duration
- **Selectable transport** — choose between WebSocket or HTTP (REST) for commands and dataref writes via `CommandSetDataRefTransport`
- **Full REST API** — list/query datarefs and commands, read values, manage flights
- **Automatic reconnection** — recovers from transient WebSocket disconnections
- **Configurable API version** — select `v1`, `v2`, or `v3` to match your X-Plane version (12.3 uses `v2`, 12.4+ uses `v3`)
- **Readiness probe** — optionally wait for a specific plugin dataref before proceeding
- **Connection closed event** — detect when X-Plane shuts down for graceful cleanup
- **Non-blocking architecture** — WebSocket reads and callback dispatching run on separate tasks; slow consumers never stall the receive loop
- **Array dataref support** — subscribe to individual indices of array datarefs (e.g. `AirbusFBW/Foo[7]`)
- **Cache warm-up** — optionally pre-resolve dataref and command IDs at startup via `PreResolveDataRefAsync` / `PreResolveCommandAsync` so the first runtime use avoids a REST round-trip

## Requirements

- .NET 10.0 or later
- X-Plane 12.1.1+ with the web API enabled

## Installation

```
dotnet add package nocscienceat.XPlaneWebConnector
```

## Quick Start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger<XPlaneWebConnector>();

// Create a named IHttpClientFactory (requires Microsoft.Extensions.Http)
var services = new ServiceCollection();
services.AddHttpClient("XPlane", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    MaxConnectionsPerServer = 30
});
using var provider = services.BuildServiceProvider();
var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

// Create and start the connector (httpClientName must match the name registered above)
var connector = new XPlaneWebConnector("localhost", 8086, CommandSetDataRefTransport.WebSocket, fireForgetOnHttpTransport: false, logger, httpClientFactory, httpClientName: "XPlane");
connector.Start();

// Subscribe to a float dataref
await connector.SubscribeAsync("sim/cockpit/autopilot/heading_mag", (float value) =>
{
    Console.WriteLine($"Heading: {value:F1}°");
});

// Set a dataref value
await connector.SetDataRefValueAsync("sim/cockpit/lights/landing_lights_on", 1);

// Send a command
await connector.SendCommandAsync("sim/autopilot/heading_up");

// Keep running...
Console.ReadLine();

// Clean shutdown
await connector.StopAsync();
```

## Core Concepts

### Interface

The library exposes one public interface, implemented by `XPlaneWebConnector`:

| Interface | Purpose |
|---|---|
| `IXPlaneWebConnector` | Subscribe to datarefs and commands, set values, send commands, availability check, connection events |

> **Note:** `IXPlaneApi` exists as an `internal` interface that documents the full X-Plane REST + WebSocket API surface. It is not intended for external consumption — all public functionality is available through `IXPlaneWebConnector`.

### Types

| Type | Description |
|---|---|
| `CommandSetDataRefTransport` | Enum selecting the transport for commands and dataref writes: `WebSocket` (default) or `Http` |
| `IXPlaneWebConnectorSettings` | Interface for connector configuration — used with DI registration via `XPlaneWebConnectorSettings` |
| `XPlaneWebConnectorSettings` | Bindable settings class with property aliases (`IpAddress`/`WebPort`) for flexible config binding. Includes `HttpClientName` for named `IHttpClientFactory` clients |
| `SubscriptionHandle` | `IDisposable` returned by `SubscribeAsync` / `SubscribeCommandAsync` — disposing removes the consumer's callback and unsubscribes from X-Plane when no consumers remain |

### Supported Dataref Types

The X-Plane API defines several dataref value types. The consumer **must** use the correct `Action<T>` overload matching the dataref's declared type:

| X-Plane `value_type` | C# callback / setter type | Array support |
|---|---|---|
| `float` | `Action<float>` / `float` | Scalar and array (`float_array`) |
| `int` | `Action<int>` / `int` | Scalar and array (`int_array`) |
| `double` | `Action<double>` / `double` | Scalar only (no `double_array` in X-Plane) |
| `data` | `Action<string>` / `string` | Scalar only (base64-encoded bytes, no array variant) |

Using the wrong type for a dataref throws an `InvalidOperationException` with a descriptive message.

## Usage

### Creating a Connector

```csharp
var connector = new XPlaneWebConnector(
    host: "localhost",                                   // X-Plane host
    port: 8086,                                          // X-Plane web API port
    commandTransport: CommandSetDataRefTransport.WebSocket, // Transport for commands & dataref writes
    fireForgetOnHttpTransport: false,                     // When true, HTTP writes return immediately without awaiting the response
    logger: logger,                                      // ILogger<XPlaneWebConnector>
    httpClientFactory: httpClientFactory,                 // IHttpClientFactory for REST calls
    readinessProbeDataRef: null,                          // Optional: wait for this dataref to exist before ready
    readinessProbeMaxRetries: 0,                          // Optional: max retries for readiness probe (0 = unlimited)
    apiVersion: "v2",                                     // API version: "v1", "v2" (12.3), or "v3" (12.4+)
    httpClientName: "XPlane"                               // Named IHttpClientFactory client (default: "" = default client)
);
```

#### Transport Selection

The `CommandSetDataRefTransport` enum controls how `SendCommandAsync` and `SetDataRefValueAsync` communicate with X-Plane:

| Transport | Commands | Dataref Writes |
|---|---|---|
| `WebSocket` (default) | WS `command_set_is_active` | WS `dataref_set_values` |
| `Http` | HTTP POST `/command/{id}/activate` | HTTP PATCH `/datarefs/{id}/value` |

WebSocket is lower-latency (persistent connection), while HTTP is stateless and provides immediate error feedback via HTTP status codes.

#### Fire-and-Forget HTTP Mode

When `fireForgetOnHttpTransport` is `true` and the transport is `Http`, all write operations (dataref sets, command activations, flight start/update) return immediately without awaiting the HTTP response. Errors are logged as warnings but never propagated to the caller. This minimises latency for high-frequency writes where the caller does not need confirmation.

When `false` (default), the HTTP response is awaited and `EnsureSuccessStatusCode()` is called, so failures surface as exceptions.

### Lifecycle

```csharp
// Start the WebSocket connection and background receive loop
connector.Start();

// ... use the connector ...

// Stop with a timeout (default 5000ms)
await connector.StopAsync(timeout: 5000);

// Dispose when done
connector.Dispose();
```

### Waiting for X-Plane Availability

The availability check is part of `IXPlaneWebConnector` — especially useful in hosted services:

```csharp
// Wait for the REST API to respond
await connector.WaitUntilAvailableAsync(cancellationToken: stoppingToken);

// Or with a custom poll interval
await connector.WaitUntilAvailableAsync(
    pollInterval: TimeSpan.FromSeconds(5),
    cancellationToken: stoppingToken
);
```

With a **readiness probe** configured, the connector also waits for a specific plugin dataref to be registered (useful for aircraft with custom plugin systems like ToLiss or FlightFactor):

```csharp
var connector = new XPlaneWebConnector(
    "localhost", 8086, CommandSetDataRefTransport.WebSocket, false, logger, httpClientFactory,
    readinessProbeDataRef: "AirbusFBW/EnableExternalPower",
    readinessProbeMaxRetries: 30
);
```

### Subscribing to Datarefs

#### Numeric Datarefs

`SubscribeAsync` returns an `IDisposable` handle. Disposing it removes the consumer's callback; when the last consumer for a dataref disposes, X-Plane is told to stop sending updates.

```csharp
IDisposable sub = await connector.SubscribeAsync("sim/cockpit2/gauges/indicators/altitude_ft_pilot", (float value) =>
{
    Console.WriteLine($"Altitude: {value:F0} ft");
});

// Later, to unsubscribe this consumer:
sub.Dispose();
```

#### Double Datarefs

Datarefs with `value_type = "double"` use a `double` callback. Double datarefs are scalar only — no array variant exists in X-Plane.

```csharp
IDisposable sub = await connector.SubscribeAsync("sim/time/total_running_time_sec", (double value) =>
{
    Console.WriteLine($"Total time: {value:F3}s");
});
```

#### Integer Datarefs

```csharp
IDisposable sub = await connector.SubscribeAsync("sim/cockpit/electrical/battery_on", (int value) =>
{
    Console.WriteLine($"Battery: {(value == 1 ? "ON" : "OFF")}");
});
```

#### String Datarefs

```csharp
IDisposable sub = await connector.SubscribeAsync("sim/aircraft/view/acf_tailnum", (string value) =>
{
    Console.WriteLine($"Tail: {value}");
});
```

#### Array Datarefs

Subscribe to individual indices using bracket notation. Only `float` and `int` datarefs support array indices (`float_array`, `int_array`):

```csharp
var sub1 = await connector.SubscribeAsync("sim/cockpit2/engine/indicators/N1_percent[0]", (float value) => Console.WriteLine($"ENG1 N1: {value:F1}%"));
var sub2 = await connector.SubscribeAsync("sim/cockpit2/engine/indicators/N1_percent[1]", (float value) => Console.WriteLine($"ENG2 N1: {value:F1}%"));
```

#### Multi-Consumer Subscriptions

Multiple panels can subscribe to the same dataref independently:

```csharp
// Panel A subscribes
var subA = await connector.SubscribeAsync("sim/cockpit2/gauges/indicators/altitude_ft_pilot", (float value) => UpdatePanelA(value));

// Panel B subscribes to the same dataref
var subB = await connector.SubscribeAsync("sim/cockpit2/gauges/indicators/altitude_ft_pilot", (float value) => UpdatePanelB(value));

// Panel A shuts down — Panel B continues receiving updates
subA.Dispose();

// Panel B shuts down — now X-Plane is told to unsubscribe
subB.Dispose();
```

### Setting Dataref Values

```csharp
// Float dataref
await connector.SetDataRefValueAsync("sim/cockpit/lights/landing_lights_on", 1.0f);

// Integer dataref
await connector.SetDataRefValueAsync("sim/cockpit/electrical/battery_on", 1);

// Double dataref
await connector.SetDataRefValueAsync("sim/time/total_running_time_sec", 123.456);

// String dataref
await connector.SetDataRefValueAsync("sim/aircraft/view/acf_tailnum", "D-ABCD");
```

### Sending Commands

```csharp
// One-shot command (press & immediate release, duration = 0)
await connector.SendCommandAsync("sim/flight_controls/landing_gear_toggle");

// Command with a specific hold duration (0–10 seconds)
await connector.SendCommandAsync("sim/annunciator/fire_test", duration: 3.0f);
```

### Subscribing to Command Activation

`SubscribeCommandAsync` follows the same pattern as dataref subscriptions: pass a command path string, receive an `IDisposable` handle for per-consumer unsubscription.

```csharp
IDisposable sub = await connector.SubscribeCommandAsync("toliss_airbus/engcommands/Master1On", isActive =>
{
    Console.WriteLine($"Engine Master 1: active={isActive}");
});

// Later, to unsubscribe:
sub.Dispose();
```

Multiple consumers can subscribe to the same command independently — the same ref-counted unsubscription logic applies as with datarefs.

### Cache Warm-Up (Optional)

The first time any dataref or command path is used, the connector resolves it to an X-Plane session ID via a REST call. `SubscribeAsync` already does this during setup, so **subscribed datarefs are pre-cached**. However, datarefs only used with `SetDataRefValueAsync` and commands used with `SendCommandAsync` are resolved lazily — the first button press or toggle switch incurs a REST round-trip.

`PreResolveDataRefAsync` and `PreResolveCommandAsync` let you warm up the cache during startup:

```csharp
// Pre-resolve commands so first button press is instant
await connector.PreResolveCommandAsync("sim/autopilot/heading_up");
await connector.PreResolveCommandAsync("sim/flight_controls/landing_gear_toggle");

// Pre-resolve write-only datarefs (never subscribed, only written to)
await connector.PreResolveDataRefAsync("sim/cockpit/lights/landing_lights_on");
```

Both methods are no-ops if the path is already cached. They support array notation (e.g. `"sim/foo[3]"`).

For bulk warm-up, resolve in parallel:

```csharp
await Task.WhenAll(
    connector.PreResolveCommandAsync("sim/autopilot/heading_up"),
    connector.PreResolveCommandAsync("sim/autopilot/heading_down"),
    connector.PreResolveDataRefAsync("sim/cockpit/lights/landing_lights_on"),
    connector.PreResolveDataRefAsync("sim/cockpit/lights/beacon_lights_on")
);
```

### Detecting Connection Loss

```csharp
connector.ConnectionClosed += () =>
{
    Console.WriteLine("X-Plane connection closed");
    // Trigger graceful shutdown, reconnect logic, etc.
};
```

### Internal API (`IXPlaneApi`)

The `IXPlaneApi` interface is `internal` and documents the full X-Plane REST + WebSocket API surface used by `XPlaneWebConnector` internally. It covers capabilities queries, dataref/command listing, flight management, batch WebSocket operations, and low-level subscribe/unsubscribe messages. See [`ApiSpecifications.md`](ApiSpecifications.md) for the complete X-Plane API reference.

## Dependency Injection

The recommended way to register the connector is via `IXPlaneWebConnectorSettings`. The library provides `XPlaneWebConnectorSettings` which can bind directly from any `IConfiguration` section. It supports property aliases (`IpAddress` → `Host`, `WebPort` → `Port`) so it can share a config section with consumer-specific settings — each class picks up what it needs and ignores the rest.

```json
{
  "XPlane": {
    "IpAddress": "127.0.0.1",
    "WebPort": 8086,
    "Transport": "Http",
    "FireForgetOnHttpTransport": true,
    "ApiVersion": "v2",
    "ReadinessProbeDataRef": "AirbusFBW/BatVolts",
    "ReadinessProbeMaxRetries": 0,
    "HttpClientName": "XPlane"
  }
}
```

Registration (requires `IHttpClientFactory` — register via `AddHttpClient`):

```csharp
var settings = builder.Configuration.GetSection("XPlane").Get<XPlaneWebConnectorSettings>()
    ?? new XPlaneWebConnectorSettings();

// Register a named IHttpClientFactory client matching settings.HttpClientName
builder.Services.AddHttpClient(settings.HttpClientName, client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    MaxConnectionsPerServer = 30
});

builder.Services.AddSingleton<IXPlaneWebConnectorSettings>(settings);
builder.Services.AddSingleton<IXPlaneWebConnector, XPlaneWebConnector>();
```

> **Note:** The `Accept` header and `SocketsHttpHandler` configuration are recommended but not strictly required — the X-Plane API returns JSON by default. Increasing `MaxConnectionsPerServer` avoids connection pool exhaustion when many REST calls (e.g. cache warm-up) run in parallel.

The DI container resolves `IXPlaneWebConnectorSettings`, `ILogger<XPlaneWebConnector>`, and `IHttpClientFactory` automatically. The connector requests a client by `HttpClientName` from the factory, so the name used in `AddHttpClient(...)` must match.

> **Tip:** Set `HttpClientName` to a non-empty value (e.g. `"XPlane"`) to avoid overriding the default `IHttpClientFactory` client. Other components that call `httpClientFactory.CreateClient()` with no name will then get a plain, uncustomised client.

Alternatively, use the raw-parameter constructor with a factory lambda:

```csharp
builder.Services.AddSingleton<IXPlaneWebConnector>(sp =>
    new XPlaneWebConnector(
        "localhost", 8086,
        CommandSetDataRefTransport.Http,      // or WebSocket
        fireForgetOnHttpTransport: true,
        sp.GetRequiredService<ILogger<XPlaneWebConnector>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        readinessProbeDataRef: "AirbusFBW/EnableExternalPower",
        readinessProbeMaxRetries: 30,
        apiVersion: "v2",
        httpClientName: "XPlane"              // must match AddHttpClient("XPlane")
    ));
```

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                  Your Application                    │
│                                                      │
│  IXPlaneWebConnector                                 │
│   (subscribe with typed callbacks,                   │
│    set values, send commands,                        │
│    availability check)                               │
└──────────────────────────┬───────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────┐
│              XPlaneWebConnector                      │
│                                                      │
│  ┌───────────────┐   ┌───────────────────────┐       │
│  │  REST Client  │   │  WebSocket Connection │       │
│  │ (HttpClient-  │   │  ┌─────────────────┐  │       │
│  │  Factory)     │   │  │  Receive Loop   │  │       │
│  │               │   │  │  (fast reader)  │  │       │
│  │  • Resolve IDs│   │  └────────┬────────┘  │       │
│  │  • Query APIs │   │           │           │       │
│  │               │   │  ┌────────▼────────┐  │       │
│  └───────────────┘   │  │ _dataChannel    │  │       │
│                      │  └────────┬────────┘  │       │
│  ID Caches           │  ┌────────▼────────┐  │       │
│  ┌─────────────────┐ │  │ Worker          │  │       │
│  │ DataRef: path→  │ │  │ (JSON parse,    │  │       │
│  │   XPlaneDataRef │ │  │  type-aware     │  │       │
│  │   Info          │ │  │  dispatch)      │  │       │
│  │ Command: path→id│ │  └────────┬────────┘  │       │
│  └─────────────────┘ │  ┌────────▼────────┐  │       │
│                      │  │ _callbacks      │  │       │
│                      │  │ Channel<Action> │  │       │
│                      │  └────────┬────────┘  │       │
│                      │  ┌────────▼────────┐  │       │
│                      │  │ Callback Task   │  │       │
│                      │  │ (invokes user   │  │       │
│                      │  │  Action()s)     │  │       │
│                      │  └─────────────────┘  │       │
│                      └───────────────────────┘       │
└──────────────────────────────────────────────────────┘
            │
            ▼
┌──────────────────────────────────────────────────────┐
│          X-Plane 12.1.1+  (REST + WebSocket)         │
└──────────────────────────────────────────────────────┘
```

**Key design decisions:**

- **Selectable transport** — `CommandSetDataRefTransport` controls whether commands and dataref writes use WebSocket (`command_set_is_active` / `dataref_set_values`) or HTTP REST (`POST /command/{id}/activate` / `PATCH /datarefs/{id}/value`). WebSocket is lower-latency; HTTP provides immediate error feedback.
- **Fire-and-forget sends** — WebSocket sends never block waiting for acknowledgements. When `fireForgetOnHttpTransport` is enabled, HTTP write operations also return immediately; errors are caught and logged by an async local function without propagating to the caller.
- **Proper resource disposal** — all `HttpResponseMessage` instances are disposed via `using` declarations to promptly return connections to the pool.
- **Decoupled three-stage pipeline** — WebSocket frames are read into a bounded `_dataChannel`; the Worker parses JSON and enqueues `Action` closures (with the dataref value captured) into an unbounded `_callbacks` channel; a dedicated callback task invokes those `Action`s. Slow consumers never stall the Worker or the receive loop.
- **Lazy ID resolution with caching** — dataref and command names are resolved to session IDs via REST on first use and cached for the lifetime of the connection. For latency-sensitive scenarios, `PreResolveDataRefAsync` / `PreResolveCommandAsync` allow eager resolution at startup.
- **Automatic reconnection** — if the WebSocket connection drops, the connector retries once after 3 seconds before signalling `ConnectionClosed`.

## Breaking Changes from 2.x

Version 3.x introduces significant API changes from the 2.x line. Unlike the "classic" `XPlaneConnector` library (which treated all datarefs as `float`), this version provides full support for X-Plane's native data types (`float`, `int`, `double`, `data`/string). Consumers **must** use the correct C# type matching the X-Plane dataref's `value_type`.

### Removed types

| Removed | Replacement |
|---|---|
| `SimDataRef` | Use the dataref path string directly |
| `SimStringDataRef` | Use the dataref path string directly |
| `SimCommand` (in connector API) | Use the command path string directly |

> **Note on `SimCommand`:** Consumer-side panels may still use a `SimCommand` helper type internally, but the connector API no longer requires it — all command methods accept plain `string` paths. This asymmetry (datarefs = pure strings, commands = optional panel-side type) is a known simplification.

### Subscription API changes

**Before (2.x):**
```csharp
var dr = new SimDataRef { DataRef = "sim/cockpit/heading" };
await connector.SubscribeAsync(dr, dataref => Use(dataref.Value)); // always float

var cmd = new SimCommand { Command = "sim/autopilot/heading_up" };
await connector.SendCommandAsync(cmd);
await connector.SubscribeCommandAsync(cmd, (c, active) => Use(c.Command, active));
```

**After (3.x):**
```csharp
await connector.SubscribeAsync("sim/cockpit/heading", (float value) => Use(value));
await connector.SubscribeAsync("sim/some/int_dataref", (int value) => Use(value));
await connector.SubscribeAsync("sim/some/double_dataref", (double value) => Use(value));
await connector.SubscribeAsync("sim/aircraft/view/acf_tailnum", (string value) => Use(value));

await connector.SendCommandAsync("sim/autopilot/heading_up");
await connector.SubscribeCommandAsync("toliss_airbus/engcommands/Master1On", isActive => Use(isActive));
```

### Callback channel changes

The internal callback channel changed from `Channel<CallbackItem>` (a discriminated union with per-type variants) to `Channel<Action>`. The Worker now captures the typed value in a closure (`() => cb(floatValue)`) and enqueues a plain `Action`. The callback task simply invokes `callback()` with no type switching.

### SetDataRefValueAsync changes

`SetDataRefValueAsync` now accepts typed values directly — no wrapper objects:

```csharp
await connector.SetDataRefValueAsync("path", 1.0f);    // float
await connector.SetDataRefValueAsync("path", 42);       // int
await connector.SetDataRefValueAsync("path", 3.14);     // double
await connector.SetDataRefValueAsync("path", "D-ABCD"); // string
```

### Type safety

Using the wrong type for a dataref now throws `InvalidOperationException` with a descriptive message (e.g., requesting `float` for a dataref that X-Plane reports as `int`). Array index notation (`[N]`) is validated against the dataref's array support — `double` and `data` types reject indices; `float` and `int` require them for array-type datarefs.

## Documentation

| Document | Description |
|---|---|
| [dataflow.md](dataflow.md) | Detailed internal data flow, threading model, and protocol interactions |
| [ApiSpecifications.md](ApiSpecifications.md) | X-Plane Web API specification reference |
| [XPlaneWebConnector.cs.callpath.md](XPlaneWebConnector.cs.callpath.md) | Call path diagram for the main partial class (lifecycle, subscribe, set, send, ID resolution) |
| [XPlaneWebConnector.Worker.cs.callpath.md](XPlaneWebConnector.Worker.cs.callpath.md) | Call path diagram for the Worker (subscription state, inbound dispatch) |
| [XPlaneWebConnector.Transport.cs.callpath.md](XPlaneWebConnector.Transport.cs.callpath.md) | Call path diagram for WebSocket transport (receive loop, send helper) |
| [XPlaneWebConnector.CallbackChannel.cs.callpath.md](XPlaneWebConnector.CallbackChannel.cs.callpath.md) | Call path diagram for the callback channel (final dispatch to consumers) |
| [XPlaneWebConnector.Utility.cs.callpath.md](XPlaneWebConnector.Utility.cs.callpath.md) | Call path diagram for utility methods (path parsing, byte decoding) |

## License

MIT — see [LICENSE](LICENSE) for details.
