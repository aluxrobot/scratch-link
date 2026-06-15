# Serial Keep-Alive Implementation Guide

## Overview

AluxLabs Link's serial transport supports **keep-alive** to prevent device timeout. Some devices (e.g. Codetinker over CH340) stop responding unless the host keeps feeding them specific packets on a regular cadence.

Keep-alive is a **client-declared contract**: on `connect` the client supplies a list of periodic packets, and Link sends each one verbatim on its own timer. Link is device-agnostic — it never inspects, frames, or generates payloads; it only delivers what the client declared, on schedule.

## Problem Statement

Some hardware needs more than one kind of periodic packet, each with its own watchdog:

- A static **liveness** packet (e.g. Codetinker `PING`, 5 bytes) — keeps the RX/connection watchdog happy. Content never changes.
- A dynamic **control** packet (e.g. Codetinker `SET_CTRL`, 19 bytes) — carries the current control state (motors, sensors, IMU). The device drops its output stream if it does not receive this within its RX timeout (~1s for Codetinker), and the content changes as control state changes.

During idle periods the host's application layer has nothing to send, so without keep-alive these packets stop and the device times out. A single-packet keep-alive cannot cover a device that needs **two** distinct periodic packets — hence the list model.

## Solution

The client declares one **keep-alive entry per periodic packet**. Each entry is `{ id, payload, intervalMs }`. Link runs an independent timer per entry and writes that entry's current payload to the serial line every `intervalMs`, verbatim.

### Key features

✅ **Multiple packets** — A device that needs N periodic packets gets N entries; they are never coalesced (avoids exceeding a transport's MTU and fragmenting).
✅ **Per-entry cadence** — Each entry has its own `intervalMs`.
✅ **Dynamic payloads** — A control packet can be refreshed at runtime via `setKeepAlivePayload` so the idle feed always reflects the latest state (no stale "snapshot" sent forever).
✅ **Device-agnostic** — Payloads are opaque bytes; correctness is the client's responsibility.
✅ **Pauseable** — The whole set can be toggled off/on with `setKeepAlive` (e.g. around a firmware update).

## Usage

### Connection request

List one entry per periodic packet the device needs. Payloads are base64-encoded:

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

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `baudRate` | int | Yes | Baud rate (e.g. 115200) |
| `peripheralType` | string | No | Device type identifier (e.g. "codetinker") |
| `keepAlive` | array | No | Periodic packets the device requires. Omit or `[]` = keep-alive disabled. |

**Keep-alive entry:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Unique id within the list; target for `setKeepAlivePayload`. |
| `payload` | string | No | Base64 bytes sent each tick, verbatim. May be set later via `setKeepAlivePayload`; empty = sends nothing yet. |
| `intervalMs` | int | Yes | Positive send cadence. Choose well under the device's RX timeout. |

### Examples

#### Codetinker (PING + SET_CTRL)
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

#### Generic device (no keep-alive)
```json
{
  "baudRate": 9600
}
```

#### Single static packet
```jsonc
{
  "baudRate": 57600,
  "peripheralType": "custom",
  "keepAlive": [
    { "id": "heartbeat", "payload": "<base64>", "intervalMs": 100 }
  ]
}
```

## Choosing a cadence

Set `intervalMs` comfortably **under** the device's RX timeout so a missed or slightly-late tick still lands in time. For Codetinker the firmware RX timeout is **~1 second** (the device expects a host packet within 1s of its own transmit), so **300ms** is used — roughly three sends per timeout window. A cadence at or above the timeout (e.g. 1000ms) leaves no margin and is unsafe.

## Dynamic payloads

A control packet reflects mutable state, so resending a connect-time snapshot forever would replay stale state (e.g. revert a motor that has since stopped). When control state changes, the client refreshes the entry:

```jsonc
{ "method": "setKeepAlivePayload", "params": { "id": "ctrl", "payload": "<base64 new SET_CTRL>" } }
// → { "id": "ctrl", "applied": true }
```

The cadence is unchanged; the entry sends the new bytes from its next tick. A static packet (e.g. `ping`) never needs this.

## How it works

```
Time: 0ms    → connect: entries [ping@300ms, ctrl@300ms] configured + started
Time: 300ms  → ping timer fires → write PING (verbatim)
Time: 300ms  → ctrl timer fires → write current SET_CTRL (verbatim)
Time: 600ms  → ping → PING ; ctrl → SET_CTRL
   ...
(client calls setKeepAlivePayload("ctrl", X))
Time: Nms    → ctrl timer fires → write X   ← picks up the new payload
```

Each entry fires on its own `intervalMs` **regardless of client writes** — there is no resend-of-last-write and no idle budget. The only interaction with client writes is a shared write lock: a keep-alive tick is skipped if a client `write` is in progress at that instant (so two writes never overlap and corrupt the stream). It is **not** skipped just because a write happened recently.

### Architecture

```
SerialSession (abstract)
├── ConfigureKeepAlive(entries)  → store the entry set (from connect)
├── StartKeepAlive()             → start one cadence Timer per entry
├── StopKeepAlive()              → stop and dispose every timer (blocks on in-flight ticks); entries kept
├── OnKeepAliveTick(entry)       → write that entry's current payload, idle-only on the write lock
├── setKeepAlive (RPC)           → toggle the whole set on/off
└── setKeepAlivePayload (RPC)    → replace one entry's payload by id

Platform-specific implementation (WinSerialSession, etc.)
├── StartKeepAlive() in DoConnect()
└── StopKeepAlive() in DoDisconnect()
```

## Firmware update safety

Keep-alive entries fire on their own cadence even during a write burst (the write lock only prevents overlap with the single write in flight, not packets between chunks). A keep-alive packet can therefore interleave between DFU chunks and corrupt the bootloader handshake.

**Always pause keep-alive explicitly around a firmware update.** Disable before bootloader entry, resume after:

```javascript
await link.send("setKeepAlive", { intervalMs: null });   // pause all entries
// ... run DFU ...
await link.send("setKeepAlive", { intervalMs: 1 });      // any positive value resumes the configured entries
```

`setKeepAlive(null)` stops every entry timer (blocking on any in-flight tick) but keeps the configured entries, so resume restarts the same set. See [SerialApiReference.md](SerialApiReference.md#setkeepalive) for the full spec.

## Diagnosing transport issues

If you suspect bytes are dropped, corrupted, or stalled in the Link layer, enable `wireTrace: true` on `connect`:

```jsonc
{
  "baudRate": 115200,
  "peripheralType": "codetinker",
  "keepAlive": [ { "id": "ping", "payload": "<base64>", "intervalMs": 300 } ],
  "wireTrace": true
}
```

Link emits hex dumps via `Trace.WriteLine` for every TX/RX, with the entry id on keep-alive sends:

```
wire-trace TX(keep-alive:ping) 5B a6 6a 01 ff 0c
wire-trace TX(keep-alive:ctrl) 19B 11 22 33 ...
```

View in [DebugView](https://learn.microsoft.com/sysinternals/downloads/debugview) or an attached debugger; compare to the client's own message log to pinpoint where data diverges.

## Troubleshooting

### Device still times out
- Verify every required packet is listed in `keepAlive` (e.g. Codetinker needs both `ping` **and** `ctrl`).
- Verify `intervalMs` is under the device's RX timeout (use 300ms for Codetinker, not 1000ms).
- For a dynamic packet, confirm the client calls `setKeepAlivePayload` so the entry has a non-empty payload.

### Device reverts to a stale state when idle
- The dynamic entry (e.g. `ctrl`) is replaying old bytes. Call `setKeepAlivePayload` whenever control state changes so the idle feed stays current.

### Firmware update hangs or fails
- Ensure keep-alive is paused with `setKeepAlive({ intervalMs: null })` before bootloader entry and resumed afterwards.
- Verify connection parameters (baud rate, etc.).

---

**Version**: 1.4
**Last Updated**: 2026-06-15
**Affected Devices**: Codetinker, and any device with sub-second timeout requirements
