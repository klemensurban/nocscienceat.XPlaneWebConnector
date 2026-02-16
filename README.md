# nocscienceat.XPlaneWebConnector

A .NET library for communicating with **X-Plane 12.1.1+** via the built-in REST and WebSocket API (`/api/v3`).

Provides a high-level interface for subscribing to datarefs, setting dataref values, and sending commands — as well as full low-level access to the X-Plane web API surface.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## Features

- **WebSocket dataref subscriptions** — receive real-time value updates with callbacks
- **Dataref writes** — set numeric and string datarefs by path or `SimDataRef` instance
- **Command execution** — send one-shot commands or control begin/end activation, with optional hold duration
- **Selectable transport** — choose between WebSocket or HTTP (REST) for commands and dataref writes via `CommandSetDataRefTransport`
- **Full REST API** — list/query datarefs and commands, read values, manage flights
- **Automatic reconnection** — recovers from transient WebSocket disconnections
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
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger<XPlaneWebConnector>();

// Create and start the connector
var connector = new XPlaneWebConnector("localhost", 8086, CommandSetDataRefTransport.WebSocket, fireForgetOnHttpTransport: false, logger, httpClientFactory);
connector.Start();

// Subscribe to a dataref
var heading = new SimDataRef { DataRef = "sim/cockpit/autopilot/heading_mag" };
await connector.SubscribeAsync(heading, (dataref, value) =>
{
    Console.WriteLine($"Heading: {value:F1}°");
});

// Set a dataref value
await connector.SetDataRefValueAsync("sim/cockpit/lights/landing_lights_on", 1f);

// Send a command
var cmd = new SimCommand("sim/autopilot/heading_up");
await connector.SendCommandAsync(cmd);

// Keep running...
Console.ReadLine();

// Clean shutdown
await connector.StopAsync();
```

## Core Concepts

### Interfaces

The library exposes three interfaces, all implemented by `XPlaneWebConnector`:

| Interface | Purpose |
|---|---|
| `IXPlaneWebConnector` | High-level: subscribe to datarefs, set values, send commands |
| `IXPlaneApi` | Low-level: full REST + WebSocket API (list datarefs/commands, manage flights, batch operations) |
| `IXPlaneAvailabilityCheck` | Startup: wait for X-Plane API readiness, detect connection loss |

### Types

| Type | Description |
|---|---|
| `SimDataRef` | Numeric dataref reference — holds the path and the latest `float` value |
| `SimStringDataRef` | String/data-type dataref reference — holds the path and the latest `string` value |
| `SimCommand` | Command reference — holds the command path and an optional description |
| `CommandSetDataRefTransport` | Enum selecting the transport for commands and dataref writes: `WebSocket` (default) or `HttpPost` |

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
    readinessProbeMaxRetries: 0                           // Optional: max retries for readiness probe (0 = unlimited)
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

Use `IXPlaneAvailabilityCheck` to block until X-Plane is ready — especially useful in hosted services:

```csharp
IXPlaneAvailabilityCheck check = connector;

// Wait for the REST API to respond
await check.WaitUntilAvailableAsync(cancellationToken: stoppingToken);

// Or with a custom poll interval
await check.WaitUntilAvailableAsync(
    pollInterval: TimeSpan.FromSeconds(5),
    cancellationToken: stoppingToken
);
```

With a **readiness probe** configured, the connector also waits for a specific plugin dataref to be registered (useful for aircraft with custom plugin systems like ToLiss or FlightFactor):

```csharp
var connector = new XPlaneWebConnector(
    "localhost", 8086, CommandSetDataRefTransport.WebSocket, logger, httpClientFactory,
    readinessProbeDataRef: "AirbusFBW/EnableExternalPower",
    readinessProbeMaxRetries: 30
);
```

### Subscribing to Datarefs

#### Numeric Datarefs

```csharp
var altitude = new SimDataRef { DataRef = "sim/cockpit2/gauges/indicators/altitude_ft_pilot" };

await connector.SubscribeAsync(altitude, (dataref, value) =>
{
    Console.WriteLine($"Altitude: {value:F0} ft");
    // dataref.Value is also updated automatically
});
```

#### String Datarefs

```csharp
var tailNumber = new SimStringDataRef { DataRef = "sim/aircraft/view/acf_tailnum" };

await connector.SubscribeAsync(tailNumber, (dataref, value) =>
{
    Console.WriteLine($"Tail: {value}");
});
```

#### Array Datarefs

Subscribe to individual indices using bracket notation:

```csharp
var engine1N1 = new SimDataRef { DataRef = "sim/cockpit2/engine/indicators/N1_percent[0]" };
var engine2N1 = new SimDataRef { DataRef = "sim/cockpit2/engine/indicators/N1_percent[1]" };

await connector.SubscribeAsync(engine1N1, (_, v) => Console.WriteLine($"ENG1 N1: {v:F1}%"));
await connector.SubscribeAsync(engine2N1, (_, v) => Console.WriteLine($"ENG2 N1: {v:F1}%"));
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
var gearToggle = new SimCommand("sim/flight_controls/landing_gear_toggle");
await connector.SendCommandAsync(gearToggle);

// Command with a specific hold duration (0–10 seconds)
var fireTest = new SimCommand("sim/annunciator/fire_test");
await connector.SendCommandAsync(fireTest, duration: 3.0f);

// With description (for documentation purposes)
var apDisconnect = new SimCommand(
    command: "sim/autopilot/disconnect",
    description: "Disconnects the autopilot"
);
await connector.SendCommandAsync(apDisconnect);
```

### Detecting Connection Loss

```csharp
connector.ConnectionClosed += () =>
{
    Console.WriteLine("X-Plane connection closed");
    // Trigger graceful shutdown, reconnect logic, etc.
};
```

### Low-Level API (`IXPlaneApi`)

For advanced use cases, cast the connector to `IXPlaneApi`:

```csharp
IXPlaneApi api = connector;

// Query capabilities
var caps = await api.GetCapabilitiesAsync();
Console.WriteLine($"X-Plane version: {caps?.XPlane?.Version}");
Console.WriteLine($"API versions: {string.Join(", ", caps?.Api?.Versions ?? [])}");

// List datarefs with filtering
var datarefs = await api.ListDataRefsAsync(
    filterNames: ["sim/cockpit/autopilot/heading_mag"],
    fields: "id,name"
);

// Get dataref count
int count = await api.GetDataRefCountAsync();

// Read a dataref value by ID
var value = await api.GetDataRefValueAsync(id: 42);

// Set a dataref value by ID
using var doc = JsonDocument.Parse("1.0");
await api.SetDataRefValueByIdAsync(id: 42, value: doc.RootElement.Clone());

// List commands
var commands = await api.ListCommandsAsync(limit: 10);

// Activate a command by ID with duration
await api.ActivateCommandAsync(id: 100, duration: 0.5f);

// Command begin/end control via WebSocket
await api.SetCommandActiveAsync(id: 100, isActive: true);   // begin
await Task.Delay(500);
await api.SetCommandActiveAsync(id: 100, isActive: false);  // end

// Flight management
using var flightDoc = JsonDocument.Parse("""{ "airport": "EDDM" }""");
await api.StartFlightAsync(flightDoc.RootElement.Clone());

// Batch dataref writes via WebSocket
await api.SetDataRefValuesByWsAsync([
    new DataRefSetEntry { Id = 42, Value = doc.RootElement.Clone() },
    new DataRefSetEntry { Id = 43, Value = doc.RootElement.Clone(), Index = 0 }
]);

// Subscribe/unsubscribe at the WebSocket level
await api.SubscribeDataRefsAsync([new DataRefSubscribeEntry { Id = 42 }]);
await api.UnsubscribeAllDataRefsAsync();

// Command activation subscriptions
await api.SubscribeCommandUpdatesAsync([100, 101], (commandId, isActive) =>
{
    Console.WriteLine($"Command {commandId} active: {isActive}");
});
await api.UnsubscribeAllCommandUpdatesAsync();
```

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
    readinessProbeMaxRetries: 30
));

builder.Services.AddSingleton<IXPlaneWebConnector>(sp =>
    sp.GetRequiredService<XPlaneWebConnector>());

builder.Services.AddSingleton<IXPlaneAvailabilityCheck>(sp =>
    sp.GetRequiredService<XPlaneWebConnector>());

builder.Services.AddSingleton<IXPlaneApi>(sp =>
    sp.GetRequiredService<XPlaneWebConnector>());
```

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                  Your Application                    │
│                                                      │
│  IXPlaneWebConnector    IXPlaneApi                   │
│   (subscribe, set,      (REST queries,               │
│    send commands)        batch WS ops)               │
└───────────┬──────────────────┬───────────────────────┘
            │                  │
            ▼                  ▼
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

For a detailed overview of the internal data flow, see [dataflow.md](dataflow.md).

## License

MIT — see [LICENSE](LICENSE) for details.
