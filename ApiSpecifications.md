# X-Plane Web API Specification

> **Source:** <https://developer.x-plane.com/article/x-plane-web-api/>
> **X-Plane** is a product of Laminar Research. Copyright © Laminar Research.
> **This document** was created by AI (Claude Opus 4.6) based on the above source for internal reference purposes.
>
> **Base URLs:**
> - REST: `http://localhost:8086/api/v3/...`
> - WebSocket: `ws://localhost:8086/api/v3`
>
> **Port override:** `--web_server_port=<port>`
> **Disable:** Settings ? Network ? "Disable Incoming Traffic" (returns `403` for all requests)

---

## Version History

| Version | X-Plane | Features |
|---------|---------|----------|
| **v1** | 12.1.1+ | Dataref list/count/read/write (REST), dataref subscribe/unsubscribe/set (WS) |
| **v2** | 12.1.4+ | Capabilities endpoint, command list/count/activate (REST), command subscribe/unsubscribe/set-active (WS) |
| **v3** | 12.4.0+ | Flight init/update (REST) |

---

## Data Schemas

### `<dataref>`

```json
{
    "id": 9952311,
    "name": "sim/cockpit2/gauges/actuators/radio_altimeter_bug_ft_pilot",
    "value_type": "float"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | `int` | Session-scoped numeric ID. Stable within a session (survives aircraft load/unload), **not** stable across X-Plane restarts. |
| `name` | `string` | Fully qualified dataref name. |
| `value_type` | `string` | One of: `float`, `double`, `int`, `int_array`, `float_array`, `data` (base64-encoded). |

### `<command>`

```json
{
    "id": 2991,
    "name": "sim/developer/toggle_autopilot_constants",
    "description": "Toggle the autopilot constants window."
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | `int` | Session-scoped numeric ID. Same stability rules as dataref IDs. |
| `name` | `string` | Fully qualified command name. |
| `description` | `string` | Human-readable description. |

---

## Request Formats

### REST Requests

**Required headers:**
- `Accept: application/json`
- `Content-Type: application/json` (when sending a body)

**Success response shape:**

```json
{ "data": <value> }
// or for collections:
{ "data": [<item>, <item>, ...] }
```

**Error response shape:**

```json
{
    "error_code": "index_out_of_range",
    "error_message": "Index is out of range"
}
```

### WebSocket Requests

**Request shape:**

```json
{
    "req_id": 123,
    "type": "...",
    "params": { ... }
}
```

| Field | Required | Type | Description |
|-------|----------|------|-------------|
| `req_id` | ? | `int` | Unique, non-recyclable request identifier. Echoed in the response. |
| `type` | ? | `string` | Operation type (e.g. `dataref_subscribe_values`). Unknown types ? `unknown_type` error. |
| `params` | ? | `object` | Operation parameters. |

**Success result:**

```json
{
    "req_id": 123,
    "type": "result",
    "success": true
}
```

**Error result:**

```json
{
    "req_id": 123,
    "type": "result",
    "success": false,
    "error_code": "...",
    "error_message": "..."
}
```

---

## REST API

### `GET /api/capabilities` *(v2+)*

Returns supported API versions and X-Plane version.

> ?? This endpoint is **not versioned** — path is `/api/capabilities`, not `/api/v3/capabilities`.

**Query params:** None

**Response:**

```json
{
    "api": {
        "versions": ["v1", "v2", "v3"]
    },
    "x-plane": {
        "version": "12.4.0"
    }
}
```

---

### `GET /datarefs`

Returns all registered datarefs.

**Query params:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `filter[name]` | `string` | ? | Filter by name (exact match). Repeatable; same-field filters use OR logic, cross-field filters use AND. |
| `start` | `int` | ? | Pagination start index (inclusive). |
| `limit` | `int` | ? | Pagination page size. |
| `fields` | `string` | ? | Comma-separated list: `id`, `name`, `value_type`, or `all` (default). |

**Response:** `{ "data": [<dataref>, ...] }`

**Errors:**

| HTTP | `error_code` | `error_message` |
|------|-------------|-----------------|
| 400 | `start_out_of_range` | Start out of range |
| 400 | `limit_out_of_range` | Limit out of range |
| 400 | `invalid_field` | Field {field} does not exist |
| 404 | `invalid_dataref_name` | Dataref {name} doesn't exist |

---

### `GET /datarefs/count`

Returns the count of registered datarefs.

**Query params:** None

**Response:** `{ "data": 9554 }`

---

### `GET /datarefs/{id}/value`

Returns the current value of a dataref (or a specific array index).

**URL params:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | `int` | ? | Dataref ID |

**Query params:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `index` | `int` | ? | Array index to read |

**Response:** `{ "data": <value> }`

**Errors:**

| HTTP | `error_code` | `error_message` |
|------|-------------|-----------------|
| 400 | `index_out_of_range` | Dataref array index out of range |
| 400 | `not_an_array` | An index was provided but the dataref is not an array |
| 404 | `invalid_dataref_id` | Dataref {id} doesn't exist |

---

### `PATCH /datarefs/{id}/value`

Sets a dataref value. For arrays: set a specific index, or set the whole array (all values required).

**URL params:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | `int` | ? | Dataref ID |

**Query params:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `index` | `int` | ? | Array index to set |

**Body:**

```json
{ "data": 210 }
// or for arrays:
{ "data": [210, 220, 240] }
```

**Response:** `200 OK` (empty body)

**Errors:**

| HTTP | `error_code` | `error_message` |
|------|-------------|-----------------|
| 400 | `index_out_of_range` | Dataref array index out of range |
| 400 | `not_an_array` | An index was provided but the dataref is not an array |
| 400 | `incompatible_data` | Provided data is too much or not enough to set all elements… |
| 400 | `invalid_body` | The request body is not valid JSON |
| 403 | `dataref_is_readonly` | Attempted to write to a read-only dataref |
| 404 | `invalid_dataref_id` | Dataref {id} doesn't exist |

---

### `GET /commands` *(v2+)*

Returns all registered commands.

**Query params:** Same as `GET /datarefs` (`filter[name]`, `start`, `limit`, `fields`).

**Response:** `{ "data": [<command>, ...] }`

**Errors:**

| HTTP | `error_code` | `error_message` |
|------|-------------|-----------------|
| 400 | `start_out_of_range` | Start out of range |
| 400 | `limit_out_of_range` | Limit out of range |
| 400 | `invalid_field` | Field {field} does not exist |
| 404 | `invalid_command_name` | Command {name} doesn't exist |

---

### `GET /commands/count` *(v2+)*

Returns the count of registered commands.

**Query params:** None

**Response:** `{ "data": 9554 }`

---

### `POST /command/{id}/activate` *(v2+)*

Runs a command for a fixed duration (fire-and-forget). Not intended for hardware button hold sequences — use WebSocket `command_set_is_active` for that.

**URL params:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | `int` | ? | Command ID |

**Body:**

```json
{ "duration": 0.5 }   // press for half a second
{ "duration": 0 }     // press & immediate release
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `duration` | `float` | ? | Seconds to hold active. Max: **10 seconds**. `0` = press & release. |

> ?? Overlapping activations of the same command have independent durations. Don't send overlapping invocations.

**Response:** `200 OK` (empty body)

**Errors:**

| HTTP | `error_code` | `error_message` |
|------|-------------|-----------------|
| 400 | `invalid_body` | The request body is not valid JSON |
| 400 | `duration_out_of_range` | Duration is out of valid range |
| 400 | `duration_missing` | Missing duration parameter |
| 404 | `invalid_command_id` | Command {id} doesn't exist |

---

### `POST /flight` *(v3+)*

Starts a new flight.

**Body:**

```json
{
    "data": {
        "ramp_start": { "airport_id": "KPDX", "ramp": "A1" },
        "aircraft": { "path": "Aircraft/Laminar Research/Boeing 737-800/b738.acf" }
    }
}
```

**Response:** `200 OK` (empty body)

**Errors:**

| HTTP | `error_code` | `error_message` |
|------|-------------|-----------------|
| 400 | `invalid_body` | The request body is not valid JSON |
| 400 | `invalid_value` | The value provided for {field_name} is not valid |
| 400 | `missing_aircraft` | Aircraft {path} doesn't exist |
| 400 | `missing_livery` | Livery {name} doesn't exist |
| 400 | `missing_scenery` | All or some scenery for the requested conditions is missing |

---

### `PATCH /flight` *(v3+)*

Updates the current flight configuration. Cannot change start location or aircraft.

**Body:**

```json
{
    "data": {
        "local_time": { "day_of_year": 100, "time_in_24_hours": 15.9 }
    }
}
```

**Response:** `200 OK` (empty body)

**Errors:** Same as `POST /flight`.

---

## WebSocket API

### `dataref_subscribe_values`

Subscribe to periodic dataref value updates (currently pushed at **10 Hz**). Idempotent — adds to existing subscriptions. A failed subscription fails all datarefs in the request.

**Request:**

```json
{
    "req_id": 9998,
    "type": "dataref_subscribe_values",
    "params": {
        "datarefs": [
            { "id": 1223, "index": 2 },
            { "id": 1224, "index": [0, 1, 2, 3] },
            { "id": 1225 }
        ]
    }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `datarefs[].id` | `int` | ? | Dataref ID |
| `datarefs[].index` | `int` or `int[]` | ? | Specific index/indices for array datarefs. Omit for scalar or full array. |

> **Index ordering:** Subscribed indices are sent in ascending index order with no sparse gaps.
> Example: subscribing to indices `[1, 5, 7]` ? updates arrive as `[val_1, val_5, val_7]`.
> If you later add index `0`, the order becomes `[val_0, val_1, val_5, val_7]`.
> **Recommendation:** Subscribe to a single index, a range, or all; if needs change, unsubscribe then resubscribe.

**Update messages** (type: `dataref_update_values`):

```json
{
    "type": "dataref_update_values",
    "data": {
        "88491": 0,
        "3994": 5,
        "199": [0, 0, 0, 4]
    }
}
```

> Only changed values are sent after the initial full snapshot.

**Errors:**

| `error_code` | `error_message` |
|-------------|-----------------|
| `index_out_of_range` | Dataref {id} array index out of range |
| `not_an_array` | An index was provided for dataref {id} but the dataref is not an array |
| `invalid_dataref_id` | Dataref {id} doesn't exist |

---

### `dataref_unsubscribe_values`

Unsubscribe from dataref updates. Idempotent — missing subscriptions are ignored.

**Request:**

```json
{
    "req_id": 1234,
    "type": "dataref_unsubscribe_values",
    "params": {
        "datarefs": [
            { "id": 1223, "index": 2 },
            { "id": 1224, "index": [0, 1, 2, 3] },
            { "id": 1225 }
        ]
    }
}
```

**Unsubscribe all:**

```json
{
    "req_id": 1234,
    "type": "dataref_unsubscribe_values",
    "params": {
        "datarefs": "all"
    }
}
```

**Errors:** Same as `dataref_subscribe_values`.

---

### `dataref_set_values`

Set one or more dataref values via WebSocket.

**Request:**

```json
{
    "req_id": 1234,
    "type": "dataref_set_values",
    "params": {
        "datarefs": [
            { "id": 88491, "value": 6 },
            { "id": 39222, "value": [2, 2, 1, 0] },
            { "id": 37555, "value": 2, "index": 1 }
        ]
    }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `datarefs[].id` | `int` | ? | Dataref ID |
| `datarefs[].value` | `number` or `number[]` | ? | Value to set |
| `datarefs[].index` | `int` | ? | Specific array index |

**Errors:**

| `error_code` | `error_message` |
|-------------|-----------------|
| `index_out_of_range` | Dataref {id} array index out of range |
| `not_an_array` | An index was provided for dataref {id} but the dataref is not an array |
| `insufficient_data` | Provided data for dataref {id} is not enough to set all elements |
| `invalid_dataref_id` | Dataref {id} doesn't exist |

---

### `command_subscribe_is_active` *(v2+)*

Subscribe to command activation status updates. Idempotent. A failed subscription fails all commands in the request.

**Request:**

```json
{
    "req_id": 9998,
    "type": "command_subscribe_is_active",
    "params": {
        "commands": [
            { "id": 1223 },
            { "id": 1224 },
            { "id": 1225 }
        ]
    }
}
```

**Update messages** (type: `command_update_is_active`):

```json
{
    "type": "command_update_is_active",
    "data": {
        "88491": false,
        "3994": false,
        "199": true
    }
}
```

**Errors:**

| `error_code` | `error_message` |
|-------------|-----------------|
| `invalid_command_id` | Command {id} doesn't exist |

---

### `command_unsubscribe_is_active` *(v2+)*

Unsubscribe from command activation updates. Idempotent.

**Request:**

```json
{
    "req_id": 1234,
    "type": "command_unsubscribe_is_active",
    "params": {
        "commands": [
            { "id": 1223 },
            { "id": 1224 },
            { "id": 1225 }
        ]
    }
}
```

**Unsubscribe all:**

```json
{
    "req_id": 1234,
    "type": "command_unsubscribe_is_active",
    "params": {
        "commands": "all"
    }
}
```

**Errors:**

| `error_code` | `error_message` |
|-------------|-----------------|
| `invalid_command_id` | Command {id} doesn't exist |

---

### `command_set_is_active` *(v2+)*

Activate or deactivate one or more commands.

**Request:**

```json
{
    "req_id": 1234,
    "type": "command_set_is_active",
    "params": {
        "commands": [
            { "id": 88491, "is_active": true, "duration": 0.5 },
            { "id": 37555, "is_active": true, "duration": 0 },
            { "id": 37556, "is_active": true },
            { "id": 39222, "is_active": false }
        ]
    }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `commands[].id` | `int` | ? | Command ID |
| `commands[].is_active` | `bool` | ? | `true` to activate, `false` to deactivate |
| `commands[].duration` | `float` | ? | Seconds until auto-deactivation. Only valid when `is_active: true`. Max: **24 hours** (internal). `0` = press & release. Omit = hold until explicitly released. |

**Behavior notes:**

- Activating an already-active command does nothing; deactivating an inactive command does nothing.
- Re-sending `duration` **replaces** the previous duration (shorter or longer).
- `is_active: false` cancels any pending duration.
- Each WebSocket connection has its own duration bookkeeping. On disconnect, all durations are erased and commands are released.

> ?? Multiple clients/plugins/hardware may operate on the same command simultaneously — no state guarantees.

**Errors:**

| `error_code` | `error_message` |
|-------------|-----------------|
| `invalid_command_id` | Command {id} doesn't exist |
| `duration_out_of_range` | Duration is out of range |

---

## Quick Reference: Message Types

| Direction | `type` | Purpose |
|-----------|--------|---------|
| Client ? Server | `dataref_subscribe_values` | Subscribe to dataref updates |
| Client ? Server | `dataref_unsubscribe_values` | Unsubscribe from dataref updates |
| Client ? Server | `dataref_set_values` | Set dataref values |
| Client ? Server | `command_subscribe_is_active` | Subscribe to command status |
| Client ? Server | `command_unsubscribe_is_active` | Unsubscribe from command status |
| Client ? Server | `command_set_is_active` | Activate/deactivate commands |
| Server ? Client | `result` | Success/error response to any request |
| Server ? Client | `dataref_update_values` | Periodic dataref value updates (10 Hz, changed values only) |
| Server ? Client | `command_update_is_active` | Command activation status changes |
