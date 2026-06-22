# Serial Transport API Reference

## JSON-RPC Methods

### discover

Discovers available serial ports.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "discover",
  "params": {
    "filters": [
      {
        "usbVendorId": 6790,
        "usbProductId": 29987,
        "pathHint": "COM"
      }
    ]
  }
}
```

**Parameters:**
- `filters` (array, optional) — Filter discovered ports. If omitted or empty, every enumerated USB serial port is reported. A port is reported when it matches **any** filter (filters are OR'd); within one filter, all specified fields must match (fields are AND'd).

**Filter fields:**
- `usbVendorId` (int, optional) — USB Vendor ID as a decimal integer. Exact match. Omit to skip this check.
- `usbProductId` (int, optional) — USB Product ID as a decimal integer. Exact match. Omit to skip this check.
- `pathHint` (string, optional) — **Case-insensitive substring** match against the OS-level port path (`info.Path`, e.g. `"COM7"` on Windows). Not a prefix, not exact, not a regex. Example: `"COM"` matches `COM3`, `COM12`, etc. Omit to skip this check.

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {}
}
```

**Notifications:** 
Discovered ports arrive as `didDiscoverPeripheral` notifications:
```json
{
  "jsonrpc": "2.0",
  "method": "didDiscoverPeripheral",
  "params": {
    "peripheralId": "port-0",
    "name": "COM7 (CH340)",
    "path": "COM7",
    "vendorId": "0x1A86",
    "productId": "0x7523",
    "rssi": 0
  }
}
```

---

### listSerialPorts

Returns a one-shot snapshot of currently matching serial ports in the response (no streamed notifications), and registers each so a subsequent `connect` can resolve its `peripheralId`. Intended for a user-facing port picker.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "listSerialPorts",
  "params": {
    "filters": [
      { "usbVendorId": 6790, "usbProductId": 29987, "pathHint": "COM" }
    ]
  }
}
```

**Parameters:**
- `filters` (array, optional) — Same shape and semantics as `discover`. Omit or empty to return every enumerated USB serial port. Filters are OR'd; fields within a filter are AND'd.

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "ports": [
      {
        "peripheralId": "COM7",
        "name": "USB-SERIAL CH340 (COM7)",
        "path": "COM7",
        "vendorId": "0x1A86",
        "productId": "0x7523"
      }
    ]
  }
}
```

- `result.ports` is the complete current snapshot. No matches → `{ "ports": [] }` (not an error).
- `peripheralId` is the COM port name; pass it back unchanged to `connect`. (Serial uses the COM path as the `peripheralId`; `discover` does the same.)
- `vendorId` / `productId` are omitted when unknown (e.g. non-USB ports). No `rssi` field (this is a snapshot, not a BLE-style discovery).
- **Side-effect-free:** does not open/close any port and does not disturb an in-progress session or its RX. Calling while connected is allowed — it returns the list only.
- **Re-call replaces the snapshot:** the registry is refreshed each call. A `peripheralId` present only in a prior snapshot becomes stale and fails `connect` with -32600.

**Errors:**
- Invalid `filters` → -32602 (detail in `error.data`).
- Method not found on an older Link → -32601; clients fall back to the `discover` path.

---

### connect

Opens a serial port connection.

**Request:**
```jsonc
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "connect",
  "params": {
    "peripheralId": "port-0",
    "baudRate": 115200,
    "dataBits": 8,
    "parity": "none",
    "stopBits": "one",
    "flowControl": "none",
    "peripheralType": "codetinker",
    "keepAlive": [
      { "id": "ping", "payload": "<base64 PING>",     "intervalMs": 300 },
      { "id": "ctrl", "payload": "<base64 SET_CTRL>", "intervalMs": 300 }
    ],
    "wireTrace": false
  }
}
```

**Parameters:**
- `peripheralId` (string, required) — Port identifier from discovery
- `baudRate` (int, required) — Baud rate (e.g., 9600, 115200)
- `dataBits` (int, optional) — Data bits (default: 8)
- `parity` (string, optional) — "none" | "even" | "odd" | "mark" | "space" (default: "none")
- `stopBits` (string, optional) — "one" | "onePointFive" | "two" (default: "one")
- `flowControl` (string, optional) — "none" | "rtsCts" | "xonXoff" (default: "none")
- `peripheralType` (string, optional) — Device type identifier ("codetinker", "connect", "technic", etc.)
- `keepAlive` (array, optional) — The periodic packets the device requires to stay alive. Each entry is sent on its **own cadence**, independent of client writes. Omit or pass `[]` to disable keep-alive. See **keep-alive entry fields** below. A device may need more than one packet (e.g. Codetinker needs both a `ping` and a control-state `ctrl` packet); list each as its own entry so they are never coalesced into one write.
- `wireTrace` (bool, optional) — Diagnostic. When `true`, Link emits per-write/per-read hex dumps via `Trace.WriteLine` (visible in DebugView or attached debugger). Off by default. Use only for transport-level debugging; the dumps include payload bytes and can be verbose.

**Keep-alive entry fields:**
- `id` (string, required) — Caller-assigned identifier, unique within the list. Used later to target this entry from `setKeepAlivePayload`.
- `payload` (string, optional) — Bytes to send each tick, base64-encoded. Written to the serial line **verbatim** (no framing/validation — the payload's correctness is the client's responsibility). May be omitted/empty initially and supplied later via `setKeepAlivePayload`; an entry with no payload sends nothing until then.
- `intervalMs` (int, required) — Send cadence in milliseconds. Must be positive. Choose well under the device's RX timeout (e.g. 300ms for Codetinker's ~1s firmware watchdog, to land ~3 sends per timeout window).

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {}
}
```

**Errors:**

The human-readable detail is carried in `error.data`; `error.message` holds the JSON-RPC category string (e.g. `"Application Error"`). Read `error.data` for the user-facing message.

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "error": {
    "code": -32500,
    "message": "Application Error",
    "data": "could not open serial port COM7: Access to the path 'COM7' is denied."
  }
}
```

---

### write

Sends data to the serial port.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "write",
  "params": {
    "message": "AQIDBA==",
    "encoding": "base64"
  }
}
```

**Parameters:**
- `message` (string, required) — Data to send (base64-encoded)
- `encoding` (string, required) — Always "base64"

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "result": {
    "sentBytes": 4
  }
}
```

**Side Effects:**
- None on keep-alive. A `write` is serialized against keep-alive sends (they share one write lock) but does not reset, cache for, or otherwise alter the keep-alive entries.

---

### startReading

Enables data reception (usually implicit after connect).

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "startReading",
  "params": {}
}
```

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "result": {}
}
```

---

### stopReading

Disables data reception (keep-alive timers continue running).

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "method": "stopReading",
  "params": {}
}
```

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "result": {}
}
```

---

### setKeepAlive

Toggle the **whole keep-alive timer set** on or off at runtime, without disconnecting. Use to pause keep-alive before a firmware update and resume it afterwards. This does **not** define the entries or their cadence — those come from `connect`'s `keepAlive` list; `setKeepAlive` only starts/stops them as a group.

**Request — pause (stop all entry timers):**
```json
{
  "jsonrpc": "2.0",
  "id": 6,
  "method": "setKeepAlive",
  "params": { "intervalMs": null }
}
```

**Request — resume (restart every configured entry on its own cadence):**
```json
{
  "jsonrpc": "2.0",
  "id": 7,
  "method": "setKeepAlive",
  "params": { "intervalMs": 1 }
}
```

**Parameters:**
- `intervalMs` (int or null, required) — Toggle signal only. `null`, `0`, or negative **pauses** all entry timers. Any positive value **resumes** them; each entry runs at its own per-entry `intervalMs` from `connect`, so this number's magnitude is not used as a cadence.

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 7,
  "result": { "intervalMs": 1 }
}
```

The `result.intervalMs` echoes the **applied** toggle value (`null` when paused). Use this to confirm the operation took effect.

**Side Effects:**
- Stop-then-start: any running entry timers are stopped (blocking on in-flight ticks) before resuming. Fully idempotent.
- The configured entries and their payloads are **preserved** across the toggle, so resume restarts the same set. To change a payload use `setKeepAlivePayload`.

---

### setKeepAlivePayload

Replace the payload of one configured keep-alive entry at runtime, so a **dynamic** packet (e.g. the current control state) stays current. The cadence is unchanged; the next tick sends the new bytes.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 8,
  "method": "setKeepAlivePayload",
  "params": { "id": "ctrl", "payload": "ESIzRFU=" }
}
```

**Parameters:**
- `id` (string, required) — The entry id from `connect`'s `keepAlive` list.
- `payload` (string, optional) — New bytes for that entry, base64-encoded. Sent verbatim from the next tick onward. Omit/empty to clear the entry's payload (it then sends nothing until set again).

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 8,
  "result": { "id": "ctrl", "applied": true }
}
```

- `result.applied` is `true` when `id` matched a configured entry, `false` otherwise. An unknown `id` is a no-op (no error), leaving every entry untouched.

**Errors:**
- Missing/empty `id` → -32602.
- Malformed (non-base64) `payload` → -32602.

---

### disconnect

Closes the serial port connection.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "id": 6,
  "method": "disconnect",
  "params": {}
}
```

**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 6,
  "result": {}
}
```

**Side Effects:**
- Stops all keep-alive timers
- Closes the port
- Does NOT fire `serialDidDisconnect` notification (client-initiated close)

---

## Notifications

### didDiscoverPeripheral

Sent for each discovered serial port during discovery.

```json
{
  "jsonrpc": "2.0",
  "method": "didDiscoverPeripheral",
  "params": {
    "peripheralId": "port-0",
    "name": "COM7 (CH340)",
    "path": "COM7",
    "vendorId": "0x1A86",
    "productId": "0x7523",
    "rssi": 0
  }
}
```

---

### serialDidReceiveData

Sent when data is received on the serial port.

```json
{
  "jsonrpc": "2.0",
  "method": "serialDidReceiveData",
  "params": {
    "message": "SG93IGFyZSB5b3U/",
    "encoding": "base64"
  }
}
```

---

### serialDidDisconnect

Sent when the connection is lost (external cause, not client-initiated).

```json
{
  "jsonrpc": "2.0",
  "method": "serialDidDisconnect",
  "params": {
    "reason": "device",
    "message": "Port was removed"
  }
}
```

**Disconnect Reasons:**
- `"device"` — Device disconnected. Triggered by any of: physical USB removal detected via a WMI device-removal watcher (fires even when the read loop is idle at `BytesToRead == 0`), a write-side `IOException` (client write or keep-alive send), or a read-loop `IOException` / external port close.
- `"error"` — Unexpected non-I/O exception in the read loop.

**Detection & recovery policy:**

AluxLabs Link **actively** detects disconnects through three independent paths, with a single-notification guard so exactly one `serialDidDisconnect` fires:
1. **WMI removal watcher** — watches the connected device's PnP id for `__InstanceDeletionEvent`; catches a physical unplug even when the RX loop is blocked.
2. **Write-side `IOException`** — a failed client write or keep-alive send escalates to disconnect.
3. **Read-loop exception** — `IOException`, or an external-close `InvalidOperationException` on the RX path.

On any of these, Link fires `serialDidDisconnect`, closes the port, and stops all keep-alive timers and the RX loop. Link does **not** retry — the client (aluxlabs) owns any reconnect/debounce policy.

---

## Complete Example: Codetinker Connection

```javascript
// 1. Discover ports
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "discover",
  "params": {}
}
// → didDiscoverPeripheral: { peripheralId: "port-0", name: "COM7 (CH340)", ... }

// 2. Connect with keep-alive (one entry per periodic packet the device needs)
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "connect",
  "params": {
    "peripheralId": "port-0",
    "baudRate": 115200,
    "peripheralType": "codetinker",
    "keepAlive": [
      { "id": "ping", "payload": "<base64 PING>",     "intervalMs": 300 },
      { "id": "ctrl", "payload": "<base64 SET_CTRL>", "intervalMs": 300 }
    ]
  }
}
// → result: {}
// → Each entry starts its own timer and sends its payload verbatim every 300ms

// 3. Send command
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "write",
  "params": {
    "message": "AQIDBA==",
    "encoding": "base64"
  }
}
// → result: { sentBytes: 4 }
// → Independent of keep-alive (shares the write lock only)

// 4. Update the dynamic control packet so idle keep-alive reflects the latest state
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "setKeepAlivePayload",
  "params": { "id": "ctrl", "payload": "ESIzRFU=" }
}
// → result: { id: "ctrl", applied: true }
// → The "ctrl" entry sends the new bytes from its next tick onward

// 5. Receive response
// ← serialDidReceiveData: { message: "BwgJCg==", encoding: "base64" }

// 6. Disconnect
{
  "jsonrpc": "2.0",
  "id": 5,
  "method": "disconnect",
  "params": {}
}
// → result: {}
// → All keep-alive timers stop
```

---

## Error Codes

The human-readable detail is in `error.data`; `error.message` is the category string below.

| Code | Message (category) | Description |
|------|---------|-------------|
| -32500 | Application Error | Port open failed, port enumeration hard failure, etc. (detail in `data`) |
| -32600 | Invalid Request | Invalid state — e.g. peripheral not registered, already connected |
| -32601 | Method not found | Unknown method (older Link server) |
| -32602 | Invalid params | Missing/invalid parameter (e.g. missing `baudRate`, malformed `filters`) |
| -32603 | Internal error | Internal error during a write/read operation |

---

## Recommendations

### For Codetinker
```jsonc
{
  "baudRate": 115200,
  "peripheralType": "codetinker",
  "keepAlive": [
    { "id": "ping", "payload": "<base64 PING>",     "intervalMs": 300 },
    { "id": "ctrl", "payload": "<base64 SET_CTRL>", "intervalMs": 300 }
  ]
}
```

### For Generic Serial Devices (no keep-alive)
```json
{
  "baudRate": 9600
}
```

### For Firmware Updates

Keep-alive entries fire on their own cadence **regardless of client writes** — there is no automatic idle-budget suppression. A keep-alive packet can therefore interleave between DFU chunks and corrupt the handshake. **Always pause keep-alive explicitly around a firmware update:** call `setKeepAlive` with `intervalMs: null` before bootloader entry, then resume after DFU completes.

```javascript
await link.send("setKeepAlive", { intervalMs: null });
// ... run DFU ...
await link.send("setKeepAlive", { intervalMs: 1 });  // any positive value resumes the configured entries
```

### For Transport-Level Debugging

Enable `wireTrace: true` on `connect` to get per-write/per-read hex dumps via `Trace.WriteLine`. Output is visible in [DebugView](https://learn.microsoft.com/sysinternals/downloads/debugview) (run as admin, "Capture Win32") or any attached debugger. Format:

```
wire-trace TX 12B 4c 4f 41 44 ...
wire-trace RX 31B 3c 1e af 00 ...
wire-trace TX(keep-alive:ping) 5B a6 6a 01 ff 0c
wire-trace TX(keep-alive:ctrl) 19B 11 22 33 ...
```

Buffers longer than 256 bytes are truncated with `…(+NB)` suffix. Compare these against the client's own per-message log to localize any drops or corruption.

---

**API Version**: 1.4  
**Last Updated**: 2026-06-15
