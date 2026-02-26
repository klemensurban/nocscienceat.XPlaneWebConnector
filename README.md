# nocscienceat.XPlaneWebConnector

A .NET library for communicating with **X-Plane 12.1.1+** via the built-in REST and WebSocket API.

Provides a high-level interface for subscribing to datarefs, setting dataref values, and sending commands — as well as full low-level access to the X-Plane web API surface.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## Features

- **WebSocket dataref subscriptions** — receive real-time value updates with callbacks; returns `IDisposable` for per-consumer unsubscription
- **WebSocket command activation subscriptions** — subscribe to command activation status updates with the same `IDisposable` pattern as datarefs
- **Disposable subscription handles** — multiple consumers can subscribe to the same dataref or command; disposing a handle removes only that consumer's callback; X-Plane is unsubscribed only when the last consumer disposes
- **Dataref writes** — set numeric and string datarefs by path or `SimDataRef` instance
- **Command execution** — send one-shot commands or control begin/end activation, with optional hold duration
- **Selectable transport** — choose between WebSocket or HTTP (REST) for commands and dataref writes via `CommandSetDataRefTransport`
- **Full REST API** — list/query datarefs and commands, read values, manage flights
- **Automatic reconnection** — recovers from transient WebSocket disconnections
- **Configurable API version** — select `v1`, `v2`, or `v3` to match your X-Plane version (12.3 uses `v2`, 12.4+ uses `v3`)
- **Readiness probe** — optionally wait for a specific plugin dataref before proceeding
- **Connection closed event** — detect when X-Plane shuts down for graceful cleanup
- **Non-blocking architecture** — WebSocket reads and callback dispatching run on separate tasks; slow consumers never stall the receive loop
- **Array dataref support** — subscribe to individual indices of array datarefs (e.g. `AirbusFBW/Foo[7]`)

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

// Create an IHttpClientFactory (requires Microsoft.Extensions.Http)
var services = new ServiceCollection();
services.AddHttpClient();
using var provider = services.BuildServiceProvider();
var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

// Create and start the connector
var connector = new XPlaneWebConnector("localhost", 8086, CommandSetDataRefTransport.WebSocket, fireForgetOnHttpTransport: false, logger, httpClientFactory);
connector.Start();

// Subscribe to a dataref
var heading = new SimDataRef { DataRef = "sim/cockpit/autopilot/heading_mag" };
await connector.SubscribeAsync(heading, dataref =>
{
    Console.WriteLine($"Heading: {dataref.Value:F1}°");
});

// Set a dataref value
await connector.SetDataRefValueAsync("sim/cockpit/lights/landing_lights_on", 1f);

// Send a command
var cmd = new SimCommand { Command = "sim/autopilot/heading_up" };
await connector.SendCommandAsync(cmd);

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
| `SimDataRef` | Numeric dataref reference — holds the path and the latest `float` value |
| `SimStringDataRef` | String/data-type dataref reference — holds the path and the latest `string` value |
| `SimCommand` | Command reference — holds the command path |
| `CommandSetDataRefTransport` | Enum selecting the transport for commands and dataref writes: `WebSocket` (default) or `Http` |
| `SubscriptionHandle` | `IDisposable` returned by `SubscribeAsync` / `SubscribeCommandAsync` — disposing removes the consumer's callback and unsubscribes from X-Plane when no consumers remain |

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
    apiVersion: "v2"                                      // API version: "v1", "v2" (12.3), or "v3" (12.4+)
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
var altitude = new SimDataRef { DataRef = "sim/cockpit2/gauges/indicators/altitude_ft_pilot" };

IDisposable sub = await connector.SubscribeAsync(altitude, dataref =>
{
    Console.WriteLine($"Altitude: {dataref.Value:F0} ft");
});

// Later, to unsubscribe this consumer:
sub.Dispose();
```

#### String Datarefs

```csharp
var tailNumber = new SimStringDataRef { DataRef = "sim/aircraft/view/acf_tailnum" };

IDisposable sub = await connector.SubscribeAsync(tailNumber, dataref =>
{
    Console.WriteLine($"Tail: {dataref.Value}");
});
```

#### Array Datarefs

Subscribe to individual indices using bracket notation:

```csharp
var engine1N1 = new SimDataRef { DataRef = "sim/cockpit2/engine/indicators/N1_percent[0]" };
var engine2N1 = new SimDataRef { DataRef = "sim/cockpit2/engine/indicators/N1_percent[1]" };

var sub1 = await connector.SubscribeAsync(engine1N1, d => Console.WriteLine($"ENG1 N1: {d.Value:F1}%"));
var sub2 = await connector.SubscribeAsync(engine2N1, d => Console.WriteLine($"ENG2 N1: {d.Value:F1}%"));
```

#### Multi-Consumer Subscriptions

Multiple panels can subscribe to the same dataref independently:

```csharp
// Panel A subscribes
var subA = await connector.SubscribeAsync(altitude, d => UpdatePanelA(d.Value));

// Panel B subscribes to the same dataref
var subB = await connector.SubscribeAsync(altitude, d => UpdatePanelB(d.Value));

// Panel A shuts down — Panel B continues receiving updates
subA.Dispose();

// Panel B shuts down — now X-Plane is told to unsubscribe
subB.Dispose();
```

### Setting Dataref Values

```csharp
// By path
await connector.SetDataRefValueAsync("sim/cockpit/lights/landing_lights_on", 1f);

// By SimDataRef instance
var parkBrake = new SimDataRef { DataRef = "sim/cockpit2/controls/parking_brake_ratio" };
await connector.SetDataRefValueAsync(parkBrake, 1.0f);

// String datarefs
await connector.SetDataRefValueAsync("sim/aircraft/view/acf_tailnum", "D-ABCD");
```

### Sending Commands

```csharp
// One-shot command (press & immediate release, duration = 0)
var gearToggle = new SimCommand { Command = "sim/flight_controls/landing_gear_toggle" };
await connector.SendCommandAsync(gearToggle);

// Command with a specific hold duration (0–10 seconds)
var fireTest = new SimCommand { Command = "sim/annunciator/fire_test" };
await connector.SendCommandAsync(fireTest, duration: 3.0f);
```

### Subscribing to Command Activation

`SubscribeCommandAsync` follows the same pattern as dataref subscriptions: pass a `SimCommand`, receive an `IDisposable` handle for per-consumer unsubscription.

```csharp
var engineMaster = new SimCommand { Command = "toliss_airbus/engcommands/Master1On" };

IDisposable sub = await connector.SubscribeCommandAsync(engineMaster, (cmd, isActive) =>
{
    Console.WriteLine($"Command {cmd.Command}: active={isActive}");
});

// Later, to unsubscribe:
sub.Dispose();
```

Multiple consumers can subscribe to the same command independently — the same ref-counted unsubscription logic applies as with datarefs.

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

Register the connector in a DI container (e.g. with `Microsoft.Extensions.Hosting`):

```csharp
builder.Services.AddSingleton<XPlaneWebConnector>(sp =>
    new XPlaneWebConnector(
        "localhost", 8086,
        CommandSetDataRefTransport.Http,      // or WebSocket
        fireForgetOnHttpTransport: true,      // fire-and-forget HTTP writes
        sp.GetRequiredService<ILogger<XPlaneWebConnector>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        readinessProbeDataRef: "AirbusFBW/EnableExternalPower",
        readinessProbeMaxRetries: 30,
        apiVersion: "v2"                      // "v2" for X-Plane 12.3, "v3" for 12.4+
    ));

builder.Services.AddSingleton<IXPlaneWebConnector>(sp =>
sp.GetRequiredService<XPlaneWebConnector>());
```

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                  Your Application                    │
│                                                      │
│  IXPlaneWebConnector                                 │
│   (subscribe, set values,                            │
│    send commands,                                    │
│    availability check)                               │
└──────────────────────────┬───────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────┐
│              XPlaneWebConnector                      │
│                                                      │
│  ┌───────────────┐   ┌───────────────────────┐       │
│  │  REST Client  │   │  WebSocket Connection │       │
│  │  (HttpClient) │   │  ┌─────────────────┐  │       │
│  │               │   │  │  Receive Loop   │  │       │
│  │  • Resolve IDs│   │  │  (fast reader)  │  │       │
│  │  • Query APIs │   │  └────────┬────────┘  │       │
│  │               │   │           │           │       │
│  └───────────────┘   │  ┌────────▼────────┐  │       │
│                      │  │ Message Queue   │  │       │
│  ID Caches           │  │ (Channel<T>)    │  │       │
│  ┌─────────────────┐ │  └────────┬────────┘  │       │
│  │ DataRef: path→id│ │  ┌────────▼────────┐  │       │
│  │ Command: path→id│ │  │ Processing Task │  │       │
│  └─────────────────┘ │  │ (dispatches     │  │       │
│                      │  │  callbacks)     │  │       │
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
- **Decoupled receive and processing** — WebSocket frames are read as fast as possible into a bounded `Channel<byte[]>`; a separate background task drains the queue and invokes callbacks. This ensures slow consumers (e.g. serial port writes) never stall the WebSocket read.
- **Lazy ID resolution with caching** — dataref and command names are resolved to session IDs via REST on first use and cached for the lifetime of the connection.
- **Automatic reconnection** — if the WebSocket connection drops, the connector retries once after 3 seconds before signalling `ConnectionClosed`.

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
