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
- **One-line DI registration** — `services.AddXPlaneWebConnector(configuration)` wires up settings, `HttpClient`, and the connector singleton in a single call
- **Cache warm-up** — optionally pre-resolve dataref and command IDs at startup via `PreResolveDataRefAsync` / `PreResolveCommandAsync` so the first runtime use avoids a REST round-trip
- **Virtual datarefs** — extensible `xplanewebconnector/*` synthetic datarefs that derive values from real X-Plane datarefs (e.g. teleport detection); consumers subscribe and write with the standard `SubscribeAsync` / `SetDataRefValueAsync` patterns; register custom providers via `RegisterVirtualDataRef<T>`; providers optionally handle writes by overriding `OnValueWritten`

## Requirements

- .NET 10.0 or later
- X-Plane 12.1.1+ with the web API enabled

## Installation

```
dotnet add package nocscienceat.XPlaneWebConnector
```

## Quick Start

The fastest way to get started is with the `AddXPlaneWebConnector` extension method and a `HostApplicationBuilder`:

```csharp
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// One-line registration: binds settings from the "XPlane" config section,
// registers a named HttpClient, and adds IXPlaneWebConnector as a singleton.
builder.Services.AddXPlaneWebConnector(builder.Configuration);

var host = builder.Build();
var connector = host.Services.GetRequiredService<IXPlaneWebConnector>();
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

> **Without a host builder?** See [Manual Registration](#manual-registration) below for standalone `ServiceCollection` usage.

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
| `IVirtualDataRefProvider<T>` | Public interface for custom virtual dataref providers — implement this to create synthetic datarefs under the `xplanewebconnector/` path prefix |

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

### Virtual Datarefs

Virtual datarefs are synthetic values derived from real X-Plane datarefs, exposed under the `xplanewebconnector/` path prefix. Consumers subscribe using the standard `SubscribeAsync` overloads — no special API needed.


#### Built-in: Teleport Detection

The connector includes a built-in `xplanewebconnector/teleport` virtual dataref that fires an `int` value when the aircraft position jumps:

| Value | Meaning |
|-------|---------|
| `1` | Distance > 1 000 m |
| `2` | Distance > 2 000 m |
| `3` | Distance > 50 km |

The provider subscribes to `sim/flightmodel/position/latitude` and `sim/flightmodel/position/longitude` lazily (on first consumer) and uses squared equirectangular distance thresholds (no `sqrt`).

```csharp
await connector.SubscribeAsync("xplanewebconnector/teleport", (int level) =>
{
    Console.WriteLine($"Teleport detected! Level={level}");
});
```

#### Built-in: AirbusFBW Alive Handshake

The connector includes a built-in `xplanewebconnector/AirbusFBWalive` virtual dataref that verifies the ToLiss AirbusFBW plugin is responsive after a teleport or scenery reload.

- **How it works:**
  Writing any `int` value to this dataref triggers a round-trip handshake:
  1. Sets the LightDome dataref to DIM (1), waits for confirmation
  2. Sets it to OFF (0), waits for confirmation
  3. If both succeed, emits `1` to all subscribers; on failure, emits `0`
  4. If a handshake is already running, further writes are ignored until it completes

- **Usage example:**
```csharp
// Wait for ToLiss plugin to be alive after scenery reload
var tcs = new TaskCompletionSource<int>();
var sub = await connector.SubscribeAsync("xplanewebconnector/AirbusFBWalive", v => { if (v == 1) tcs.TrySetResult(v); });
await connector.SetDataRefValueAsync("xplanewebconnector/AirbusFBWalive", 1);
await tcs.Task;
sub.Dispose();
```

- **Intended use:**
Panels should use this handshake before resetting hardware after a teleport with scenery reload, to ensure X-Plane and the plugin are ready to report switch/knob states.

#### Custom Virtual Datarefs

Implement `IVirtualDataRefProvider<T>` and register it before the first subscription:

```csharp
public class GroundSpeedKtsProvider : IVirtualDataRefProvider<float>
{
    public string Prefix => "groundspeed_kts";

    public async Task InitializeAsync(IXPlaneWebConnector connector, Action<float> emit)
    {
        await connector.SubscribeAsync("sim/flightmodel/position/groundspeed",
            (float mPerSec) => emit(mPerSec * 1.94384f));
    }
}

// Register at startup (before any panel subscribes)
connector.RegisterVirtualDataRef(new GroundSpeedKtsProvider());

// Any consumer subscribes normally
await connector.SubscribeAsync("xplanewebconnector/groundspeed_kts", (float kts) =>
{
    Console.WriteLine($"GS: {kts:F0} kts");
});
```

Key rules for providers:
- `InitializeAsync` is called once, lazily, when the first consumer subscribes
- The `emit` delegate fans out to all consumers through the same callback channel as native datarefs
- Subscriptions to real X-Plane datarefs inside `InitializeAsync` must NOT be disposed
- `CallbackType` is derived automatically from `T` via a default interface implementation
- Override `OnValueWritten(T value)` to handle writes via `SetDataRefValueAsync`; the default is a no-op (read-only)
- `OnValueWritten` runs on the Callback Task (same thread as `emit` callbacks) — no locks needed

#### Writable Virtual Datarefs

Providers can optionally accept writes by overriding `OnValueWritten`. Consumers write using the standard `SetDataRefValueAsync` — the connector intercepts the `xplanewebconnector/` prefix and routes through the callback channel:

```csharp
public class BrightnessOverrideProvider : IVirtualDataRefProvider<float>
{
    private Action<float>? _emit;
    public string Prefix => "brightness_override";

    public Task InitializeAsync(IXPlaneWebConnector connector, Action<float> emit)
    {
        _emit = emit;
        return Task.CompletedTask;
    }

    public void OnValueWritten(float value)
    {
        // Clamp and echo back to all subscribers
        _emit?.Invoke(Math.Clamp(value, 0f, 1f));
    }
}

// Register
connector.RegisterVirtualDataRef(new BrightnessOverrideProvider());

// Write — routed to OnValueWritten on the Callback Task
await connector.SetDataRefValueAsync("xplanewebconnector/brightness_override", 0.75f);
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

### Recommended: `AddXPlaneWebConnector` Extension

The library provides an `IServiceCollection` extension that handles all registration in a single call:

```csharp
builder.Services.AddXPlaneWebConnector(builder.Configuration);
```

This reads `XPlaneWebConnectorSettings` from the `"XPlane"` configuration section, registers a named `HttpClient` with an `Accept: application/json` header and a `SocketsHttpHandler`, and adds `IXPlaneWebConnector` as a singleton.

The extension method accepts optional parameters:

| Parameter | Default | Description |
|---|---|---|
| `sectionName` | `"XPlane"` | Configuration section to bind settings from |
| `maxConnectionsPerServer` | `30` | Maximum concurrent connections per server for the `SocketsHttpHandler` |

```csharp
// Custom section name and connection limit
builder.Services.AddXPlaneWebConnector(builder.Configuration, sectionName: "FlightSim", maxConnectionsPerServer: 50);
```

### Configuration

`XPlaneWebConnectorSettings` can bind directly from any `IConfiguration` section. It supports property aliases (`IpAddress` → `Host`, `WebPort` → `Port`) so it can share a config section with consumer-specific settings — each class picks up what it needs and ignores the rest.

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

> **Note:** The `Accept` header and `SocketsHttpHandler` configuration are recommended but not strictly required — the X-Plane API returns JSON by default. Increasing `MaxConnectionsPerServer` avoids connection pool exhaustion when many REST calls (e.g. cache warm-up) run in parallel.

> **Tip:** Set `HttpClientName` to a non-empty value (e.g. `"XPlane"`) to avoid overriding the default `IHttpClientFactory` client. Other components that call `httpClientFactory.CreateClient()` with no name will then get a plain, uncustomised client.

### Manual Registration

If you need full control over the `HttpClient` or handler configuration, register the components individually:

```csharp
var settings = builder.Configuration.GetSection("XPlane").Get<XPlaneWebConnectorSettings>()
    ?? new XPlaneWebConnectorSettings();

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

### What's New in 3.4.0

- **Writable virtual datarefs** — `SetDataRefValueAsync` now intercepts `xplanewebconnector/` paths and routes to `IVirtualDataRefProvider<T>.OnValueWritten`; writes are dispatched through the callback channel for thread safety
- **`OnValueWritten(T)` default interface method** — providers override to handle writes; default is no-op (read-only)

### What's New in 3.3.0

- **Virtual datarefs** — extensible `xplanewebconnector/*` synthetic datarefs with the `IVirtualDataRefProvider<T>` public interface
- **Built-in teleport detection** — `xplanewebconnector/teleport` fires on position jumps > 1 km
- **`RegisterVirtualDataRef<T>`** added to `IXPlaneWebConnector` for external extensibility

## License

MIT — see [LICENSE](LICENSE) for details.
