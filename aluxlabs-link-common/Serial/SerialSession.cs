// <copyright file="SerialSession.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Serial;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fleck;
using AluxLabs.Link.Extensions;
using AluxLabs.Link.JsonRpc;

/// <summary>
/// Cross-platform base for a USB Serial transport session. Uses Serial-specific
/// notification names (<c>serialDidReceiveData</c>, <c>serialDidDisconnect</c>)
/// so callers cannot confuse Serial events with BLE characteristic events or
/// BT message events.
/// </summary>
/// <typeparam name="TPort">Platform-specific port handle, passed back to <see cref="DoConnect(TPort, SerialOpenParams)"/>.</typeparam>
internal abstract class SerialSession<TPort> : PeripheralSession<TPort, string>
    where TPort : class
{
    // Serializes DoWrite calls so two writes never overlap and corrupt the stream.
    private readonly SemaphoreSlim writeSemaphore = new SemaphoreSlim(1, 1);

    // Guards keep-alive lifecycle fields shared with the timer callbacks.
    private readonly object stateLock = new object();

    // The periodic packets the device requires; each runs on its own cadence timer. Guarded by stateLock.
    private readonly List<KeepAliveEntry> keepAliveEntries = new ();
    private bool keepAliveActive;

    // volatile so threads outside stateLock see the latest value on the hot path.
    private volatile bool wireTrace;

    /// <summary>
    /// Initializes a new instance of the <see cref="SerialSession{TPort}"/> class.
    /// </summary>
    /// <inheritdoc cref="Session.Session(IWebSocketConnection)"/>
    public SerialSession(IWebSocketConnection webSocket)
        : base(webSocket)
    {
        this.Handlers["discover"] = this.HandleDiscover;
        this.Handlers["listSerialPorts"] = this.HandleListSerialPorts;
        this.Handlers["write"] = this.HandleWrite;
        this.Handlers["disconnect"] = this.HandleDisconnect;
        this.Handlers["startReading"] = this.HandleStartReading;
        this.Handlers["stopReading"] = this.HandleStopReading;
        this.Handlers["setKeepAlive"] = this.HandleSetKeepAlive;
        this.Handlers["setKeepAlivePayload"] = this.HandleSetKeepAlivePayload;
        this.Handlers["triggerDTRReset"] = this.HandleTriggerDTRReset;
    }

    /// <inheritdoc/>
    protected override string GeneratePeripheralId(string peripheralAddress)
    {
        // A serial port path is stable and non-sensitive, so use it directly as the ID rather than an anonymized GUID.
        return peripheralAddress;
    }

    /// <summary>
    /// Implement the JSON-RPC "discover" request. Parses the filter list and
    /// kicks off platform-specific enumeration. Discovered ports are streamed
    /// back via <see cref="OnPortDiscovered"/>.
    /// </summary>
    /// <param name="methodName">The name of the method being called ("discover").</param>
    /// <param name="args">A JSON object optionally containing a <c>filters</c> array.</param>
    /// <returns>A <see cref="Task"/> resolving to an empty result; discoveries are streamed via notifications.</returns>
    protected async Task<object> HandleDiscover(string methodName, JsonElement? args)
    {
        var filters = ParseFilters(args);
        Trace.WriteLine($"received serial discover request with {filters.Count} filter(s)");

        this.ClearDiscoveredPeripherals();
        var ports = await this.DoEnumeratePorts(filters);
        foreach (var port in ports)
        {
            await this.OnPortDiscovered(port.Port, port.Path, port.DisplayName, port.VendorIdHex, port.ProductIdHex);
        }

        return new Dictionary<string, object>();
    }

    /// <summary>
    /// Implement the JSON-RPC "listSerialPorts" request. Returns a one-shot snapshot of
    /// currently matching ports in the response (no streamed notifications) and registers
    /// each so a subsequent <c>connect</c> can resolve its peripheral ID.
    /// </summary>
    /// <param name="methodName">The name of the method being called ("listSerialPorts").</param>
    /// <param name="args">A JSON object optionally containing a <c>filters</c> array.</param>
    /// <returns>A <see cref="Task"/> resolving to a <c>ports</c> array of the matching ports.</returns>
    protected async Task<object> HandleListSerialPorts(string methodName, JsonElement? args)
    {
        var filters = ParseFilters(args);
        Trace.WriteLine($"received listSerialPorts request with {filters.Count} filter(s)");

        // Refresh the registry so an ID from a prior snapshot fails connect (-32600) instead of opening a vanished port.
        this.ClearDiscoveredPeripherals();
        var ports = await this.DoEnumeratePorts(filters);

        var items = new List<SerialPortListItem>(ports.Count);
        foreach (var port in ports)
        {
            var peripheralId = this.RegisterPeripheral(port.Port, port.Path);
            items.Add(new SerialPortListItem
            {
                PeripheralId = peripheralId,
                Name = port.DisplayName,
                Path = port.Path,
                VendorId = port.VendorIdHex,
                ProductId = port.ProductIdHex,
            });
        }

        return new SerialPortListResult { Ports = items };
    }

    /// <summary>
    /// Platform-specific one-shot enumeration of currently present serial ports matching
    /// the filters. Returns a complete snapshot in one call; does not stream.
    /// </summary>
    /// <param name="filters">The filter list from the client. Empty means "match all".</param>
    /// <returns>A <see cref="Task"/> resolving to the matching ports.</returns>
    protected abstract Task<IReadOnlyList<EnumeratedPort>> DoEnumeratePorts(IReadOnlyList<SerialDiscoveryFilter> filters);

    /// <inheritdoc/>
    protected override Task<object> DoConnect(TPort port, JsonElement? args)
    {
        var openParams = ParseOpenParams(args);
        this.wireTrace = openParams.WireTrace;
        if (this.wireTrace)
        {
            Trace.WriteLine("wire-trace: enabled for this session");
        }

        // Configure before the platform DoConnect starts the timers so the first tick already sees the entries.
        this.ConfigureKeepAlive(openParams.KeepAlive);

        return this.DoConnect(port, openParams);
    }

    /// <summary>
    /// Platform-specific implementation for opening the given port. On success,
    /// RX should be active and incoming bytes should be reported via
    /// <see cref="DidReceiveData"/>.
    /// </summary>
    /// <param name="port">The port handle previously registered via <see cref="OnPortDiscovered"/>.</param>
    /// <param name="openParams">Open parameters extracted from the connect request.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected abstract Task<object> DoConnect(TPort port, SerialOpenParams openParams);

    /// <summary>
    /// JSON-RPC <c>write</c> handler. Sends a client message to the serial line under the write lock.
    /// </summary>
    /// <param name="methodName">Dispatched method name.</param>
    /// <param name="args">Decoded request params.</param>
    /// <returns><c>sentBytes</c> wrapper.</returns>
    protected async Task<object> HandleWrite(string methodName, JsonElement? args)
    {
        if (args == null)
        {
            throw JsonRpc2Error.InvalidParams("write requires a message buffer").ToException();
        }

        var buffer = EncodingHelpers.DecodeBuffer(args.Value);

        await this.writeSemaphore.WaitAsync().ConfigureAwait(false);
        int sentBytes;
        try
        {
            if (this.wireTrace)
            {
                Trace.WriteLine($"wire-trace TX {buffer.Length}B {FormatHex(buffer)}");
            }

            sentBytes = await this.DoWrite(buffer).ConfigureAwait(false);
        }
        finally
        {
            this.writeSemaphore.Release();
        }

        return new Dictionary<string, int> { ["sentBytes"] = sentBytes };
    }

    /// <summary>
    /// Platform-specific implementation for sending bytes to the port.
    /// </summary>
    /// <param name="data">The bytes to send.</param>
    /// <returns>The number of bytes actually written.</returns>
    protected abstract Task<int> DoWrite(byte[] data);

    /// <summary>
    /// Implement the JSON-RPC "disconnect" request. Closes the port without
    /// firing a <c>serialDidDisconnect</c> notification (that is reserved for
    /// external-cause disconnects).
    /// </summary>
    /// <param name="methodName">The name of the method being called ("disconnect").</param>
    /// <param name="args">Unused.</param>
    /// <returns>An empty result.</returns>
    protected async Task<object> HandleDisconnect(string methodName, JsonElement? args)
    {
        await this.DoDisconnect();
        return new Dictionary<string, object>();
    }

    /// <summary>
    /// Platform-specific implementation for closing the port.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected abstract Task DoDisconnect();

    /// <summary>
    /// Implement the JSON-RPC "startReading" request. RX is enabled automatically
    /// on connect, so the default implementation is a no-op. Subclasses may
    /// override to re-enable RX after a <c>stopReading</c>.
    /// </summary>
    /// <param name="methodName">The name of the method being called ("startReading").</param>
    /// <param name="args">Unused.</param>
    /// <returns>An empty result.</returns>
    protected virtual Task<object> HandleStartReading(string methodName, JsonElement? args)
    {
        return Task.FromResult<object>(new Dictionary<string, object>());
    }

    /// <summary>
    /// Implement the JSON-RPC "stopReading" request. Default is a no-op.
    /// </summary>
    /// <param name="methodName">The name of the method being called ("stopReading").</param>
    /// <param name="args">Unused.</param>
    /// <returns>An empty result.</returns>
    protected virtual Task<object> HandleStopReading(string methodName, JsonElement? args)
    {
        return Task.FromResult<object>(new Dictionary<string, object>());
    }

    /// <summary>
    /// JSON-RPC <c>setKeepAlive</c> handler. Toggles the whole keep-alive timer set on or off:
    /// a positive <c>intervalMs</c> resumes every configured entry on its own cadence; null/0/negative pauses all.
    /// Idempotent (stop-then-start). Response echoes the applied value (null when paused).
    /// </summary>
    /// <param name="methodName">Dispatched method name.</param>
    /// <param name="args">Decoded request params.</param>
    /// <returns>Echo of the applied value.</returns>
    protected Task<object> HandleSetKeepAlive(string methodName, JsonElement? args)
    {
        int? requested = null;
        if (args != null)
        {
            var prop = args.Value.TryGetProperty("intervalMs");
            if (prop.HasValue && prop.Value.ValueKind != JsonValueKind.Null)
            {
                requested = prop.Value.GetInt32();
            }
        }

        // Stop-then-start makes the call idempotent regardless of current state.
        this.StopKeepAlive();

        int? applied = null;
        if (requested.HasValue && requested.Value > 0)
        {
            this.StartKeepAlive();
            applied = requested.Value;
        }

        var appliedText = applied?.ToString() ?? "null";
        Trace.WriteLine($"keep-alive: setKeepAlive applied intervalMs={appliedText}");
        return Task.FromResult<object>(new Dictionary<string, object> { ["intervalMs"] = applied });
    }

    /// <summary>
    /// JSON-RPC <c>setKeepAlivePayload</c> handler. Replaces the payload of one configured entry at runtime so a
    /// dynamic packet stays current; the cadence is unchanged and the next tick sends the new bytes. An unknown id
    /// leaves every entry untouched and reports <c>applied: false</c>.
    /// </summary>
    /// <param name="methodName">Dispatched method name.</param>
    /// <param name="args">Decoded request params.</param>
    /// <returns>Echo of the id and whether it matched a configured entry.</returns>
    protected Task<object> HandleSetKeepAlivePayload(string methodName, JsonElement? args)
    {
        var id = args?.TryGetProperty("id")?.GetString();
        if (string.IsNullOrEmpty(id))
        {
            throw JsonRpc2Error.InvalidParams("setKeepAlivePayload requires an id").ToException();
        }

        var payload = DecodeKeepAlivePayload(args?.TryGetProperty("payload")?.GetString());

        var applied = false;
        lock (this.stateLock)
        {
            foreach (var entry in this.keepAliveEntries)
            {
                if (entry.Id == id)
                {
                    entry.Payload = payload;
                    applied = true;
                    break;
                }
            }
        }

        Trace.WriteLine($"keep-alive: setKeepAlivePayload id={id} applied={applied}");
        return Task.FromResult<object>(new Dictionary<string, object> { ["id"] = id, ["applied"] = applied });
    }

    /// <summary>
    /// JSON-RPC <c>triggerDTRReset</c> handler. Acquires the write semaphore so no
    /// write overlaps the DTR pulse sequence, then delegates to <see cref="DoTriggerDTRReset"/>.
    /// </summary>
    /// <param name="methodName">Dispatched method name.</param>
    /// <param name="args">Unused.</param>
    /// <returns>An empty result, returned after the DTR pulse sequence completes.</returns>
    protected async Task<object> HandleTriggerDTRReset(string methodName, JsonElement? args)
    {
        Trace.WriteLine("triggerDTRReset: executing DTR pulse");

        await this.writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await this.DoTriggerDTRReset().ConfigureAwait(false);
        }
        finally
        {
            this.writeSemaphore.Release();
        }

        return new Dictionary<string, object>();
    }

    /// <summary>
    /// Platform-specific implementation for the DTR reset pulse. Asserts DTR for 50 ms
    /// then releases it. Must throw <see cref="JsonRpc2Exception"/> on failure so the
    /// caller receives a well-formed JSON-RPC error response.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected abstract Task DoTriggerDTRReset();

    /// <summary>
    /// Report received bytes to the client as a <c>serialDidReceiveData</c>
    /// notification. The payload is base64-encoded.
    /// </summary>
    /// <param name="data">The bytes received.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected async Task DidReceiveData(byte[] data)
    {
        if (this.wireTrace)
        {
            Trace.WriteLine($"wire-trace RX {data.Length}B {FormatHex(data)}");
        }

        var encoded = EncodingHelpers.EncodeBuffer(data, "base64");

        await this.SendNotification("serialDidReceiveData", new SerialDataReceived
        {
            Encoding = "base64",
            Message = encoded,
        });
    }

    /// <summary>
    /// Report an external-cause disconnect to the client as a
    /// <c>serialDidDisconnect</c> notification. Does not fire for the
    /// client-initiated <c>disconnect</c> request.
    /// </summary>
    /// <param name="reason">One of "user", "device", "error", "shutdown".</param>
    /// <param name="message">Optional human-readable detail.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected async Task DidDisconnect(string reason, string message = null)
    {
        await this.SendNotification("serialDidDisconnect", new SerialDisconnectMessage
        {
            Reason = reason,
            Message = message,
        });
    }

    /// <summary>
    /// Track a discovered port and report it to the client. Uses
    /// <see cref="PeripheralSession{TPort, String}.RegisterPeripheral"/> to
    /// obtain a session-scoped peripheral ID.
    /// </summary>
    /// <param name="port">Platform-specific port handle.</param>
    /// <param name="path">OS-level port path used as the address (e.g. "COM7").</param>
    /// <param name="displayName">User-visible name, may include the path.</param>
    /// <param name="vendorIdHex">Vendor ID as a hex string (e.g. "0x1A86"), or null.</param>
    /// <param name="productIdHex">Product ID as a hex string (e.g. "0x7523"), or null.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected async Task OnPortDiscovered(TPort port, string path, string displayName, string vendorIdHex, string productIdHex)
    {
        var peripheralId = this.RegisterPeripheral(port, path);

        await this.SendNotification("didDiscoverPeripheral", new SerialPortDiscovered
        {
            PeripheralId = peripheralId,
            Name = displayName,
            Path = path,
            VendorId = vendorIdHex,
            ProductId = productIdHex,
            RSSI = 0,
        });
    }

    /// <summary>
    /// Replace the configured keep-alive entry set. Does not start timers; the platform layer calls
    /// <see cref="StartKeepAlive"/> once the port is open.
    /// </summary>
    /// <param name="entries">The periodic packets the device requires, or null/empty to disable keep-alive.</param>
    protected void ConfigureKeepAlive(IReadOnlyList<KeepAliveEntryParam> entries)
    {
        lock (this.stateLock)
        {
            this.keepAliveEntries.Clear();
            if (entries == null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                this.keepAliveEntries.Add(new KeepAliveEntry(entry.Id, entry.IntervalMs, entry.Payload));
            }
        }
    }

    /// <summary>
    /// Start a cadence timer for every configured entry. No-op if already running or if nothing is configured.
    /// </summary>
    protected void StartKeepAlive()
    {
        lock (this.stateLock)
        {
            if (this.keepAliveActive)
            {
                Trace.WriteLine("keep-alive: StartKeepAlive called while already active; ignoring");
                return;
            }

            if (this.keepAliveEntries.Count == 0)
            {
                return;
            }

            this.keepAliveActive = true;
            foreach (var entry in this.keepAliveEntries)
            {
                entry.Timer = new Timer(this.OnKeepAliveTick, entry, entry.IntervalMs, entry.IntervalMs);
                Trace.WriteLine($"keep-alive: started id={entry.Id} ({entry.IntervalMs}ms)");
            }
        }
    }

    /// <summary>
    /// Stop and dispose every entry's timer, blocking until any in-flight tick finishes. Keeps the configured
    /// entries so a later <see cref="StartKeepAlive"/> can resume them. Safe to call repeatedly.
    /// </summary>
    protected void StopKeepAlive()
    {
        List<Timer> toDispose;
        lock (this.stateLock)
        {
            if (!this.keepAliveActive)
            {
                return;
            }

            this.keepAliveActive = false;
            toDispose = new List<Timer>(this.keepAliveEntries.Count);
            foreach (var entry in this.keepAliveEntries)
            {
                if (entry.Timer != null)
                {
                    toDispose.Add(entry.Timer);
                    entry.Timer = null;
                }
            }
        }

        foreach (var timer in toDispose)
        {
            // Block so no send races a subsequent disconnect or port disposal.
            using var waitHandle = new ManualResetEvent(false);
            if (timer.Dispose(waitHandle))
            {
                waitHandle.WaitOne();
            }
        }

        Trace.WriteLine("keep-alive: stopped");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.DisposedValue)
        {
            // Order matters: StopKeepAlive waits for in-flight ticks before we dispose the semaphore they use.
            this.StopKeepAlive();
            this.writeSemaphore.Dispose();
        }

        base.Dispose(disposing);
    }

    private static IReadOnlyList<SerialDiscoveryFilter> ParseFilters(JsonElement? args)
    {
        var result = new List<SerialDiscoveryFilter>();

        var filtersElement = args?.TryGetProperty("filters");
        if (filtersElement == null)
        {
            return result;
        }

        if (filtersElement.Value.ValueKind != JsonValueKind.Array)
        {
            throw JsonRpc2Error.InvalidParams("'filters' must be an array").ToException();
        }

        foreach (var item in filtersElement.Value.EnumerateArray())
        {
            result.Add(new SerialDiscoveryFilter
            {
                UsbVendorId = item.TryGetProperty("usbVendorId")?.GetInt32(),
                UsbProductId = item.TryGetProperty("usbProductId")?.GetInt32(),
                PathHint = item.TryGetProperty("pathHint")?.GetString(),
            });
        }

        return result;
    }

    private static SerialOpenParams ParseOpenParams(JsonElement? args)
    {
        var baudRate = args?.TryGetProperty("baudRate")?.GetInt32();
        if (baudRate == null)
        {
            throw JsonRpc2Error.InvalidParams("connect requires baudRate").ToException();
        }

        return new SerialOpenParams
        {
            BaudRate = baudRate.Value,
            DataBits = args?.TryGetProperty("dataBits")?.GetInt32() ?? 8,
            Parity = args?.TryGetProperty("parity")?.GetString() ?? "none",
            StopBits = args?.TryGetProperty("stopBits")?.GetString() ?? "one",
            FlowControl = args?.TryGetProperty("flowControl")?.GetString() ?? "none",
            PeripheralType = args?.TryGetProperty("peripheralType")?.GetString(),
            KeepAlive = ParseKeepAlive(args),
            WireTrace = args?.TryGetProperty("wireTrace")?.GetBoolean() ?? false,
        };
    }

    private static IReadOnlyList<KeepAliveEntryParam> ParseKeepAlive(JsonElement? args)
    {
        var result = new List<KeepAliveEntryParam>();

        var element = args?.TryGetProperty("keepAlive");
        if (element == null || element.Value.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (element.Value.ValueKind != JsonValueKind.Array)
        {
            throw JsonRpc2Error.InvalidParams("'keepAlive' must be an array").ToException();
        }

        foreach (var item in element.Value.EnumerateArray())
        {
            var id = item.TryGetProperty("id")?.GetString();
            if (string.IsNullOrEmpty(id))
            {
                throw JsonRpc2Error.InvalidParams("keepAlive entry requires an id").ToException();
            }

            var intervalMs = item.TryGetProperty("intervalMs")?.GetInt32();
            if (intervalMs == null || intervalMs.Value <= 0)
            {
                throw JsonRpc2Error.InvalidParams($"keepAlive entry '{id}' requires a positive intervalMs").ToException();
            }

            result.Add(new KeepAliveEntryParam
            {
                Id = id,
                IntervalMs = intervalMs.Value,
                Payload = DecodeKeepAlivePayload(item.TryGetProperty("payload")?.GetString()),
            });
        }

        return result;
    }

    private static byte[] DecodeKeepAlivePayload(string base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw JsonRpc2Error.InvalidParams("keepAlive payload must be base64-encoded").ToException();
        }
    }

    /// <summary>
    /// Hex preview for diagnostic logs, capped at <paramref name="maxBytes"/> with a tail marker.
    /// </summary>
    private static string FormatHex(byte[] data, int maxBytes = 256)
    {
        if (data == null || data.Length == 0)
        {
            return string.Empty;
        }

        var take = data.Length <= maxBytes ? data.Length : maxBytes;
        var sb = new System.Text.StringBuilder(take * 3);
        for (var i = 0; i < take; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(data[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (data.Length > take)
        {
            sb.Append($" …(+{data.Length - take}B)");
        }

        return sb.ToString();
    }

    private async void OnKeepAliveTick(object state)
    {
        var entry = (KeepAliveEntry)state;

        byte[] data;
        lock (this.stateLock)
        {
            if (!this.keepAliveActive)
            {
                return;
            }

            data = entry.Payload;
        }

        if (data == null || data.Length == 0 || !this.IsConnected)
        {
            return;
        }

        // WaitAsync(0) makes the tick idle-only: during a write burst the semaphore is busy and we no-op.
        if (!await this.writeSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!this.keepAliveActive || !this.IsConnected)
            {
                return;
            }

            if (this.wireTrace)
            {
                Trace.WriteLine($"wire-trace TX(keep-alive:{entry.Id}) {data.Length}B {FormatHex(data)}");
            }

            await this.DoWrite(data).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Session disposed mid-tick.
        }
        catch (Exception e)
        {
            // async-void Timer callback: an escaped exception terminates the process, so log and move on.
            Trace.WriteLine($"keep-alive[{entry.Id}]: send failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try
            {
                this.writeSemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Semaphore disposed during shutdown.
            }
        }
    }

    /// <summary>
    /// Payload of a <c>serialDidReceiveData</c> notification.
    /// </summary>
    protected class SerialDataReceived
    {
        /// <summary>
        /// Gets or sets the encoding identifier; always "base64" for serial RX.
        /// </summary>
        [JsonPropertyName("encoding")]
        public string Encoding { get; set; }

        /// <summary>
        /// Gets or sets the encoded payload.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    /// <summary>
    /// Payload of a <c>serialDidDisconnect</c> notification.
    /// </summary>
    protected class SerialDisconnectMessage
    {
        /// <summary>
        /// Gets or sets the disconnect reason: "user", "device", "error", or "shutdown".
        /// </summary>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Reason { get; set; }

        /// <summary>
        /// Gets or sets an optional human-readable detail message.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Message { get; set; }
    }

    /// <summary>
    /// Payload of a <c>didDiscoverPeripheral</c> notification on the serial transport.
    /// </summary>
    protected class SerialPortDiscovered
    {
        /// <summary>
        /// Gets or sets the session-scoped peripheral ID used by the client to connect.
        /// </summary>
        [JsonPropertyName("peripheralId")]
        public string PeripheralId { get; set; }

        /// <summary>
        /// Gets or sets the user-visible name of the port.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the OS-level port path (e.g. "COM7").
        /// </summary>
        [JsonPropertyName("path")]
        public string Path { get; set; }

        /// <summary>
        /// Gets or sets the USB vendor ID as a hex string (e.g. "0x1A86"), if known.
        /// </summary>
        [JsonPropertyName("vendorId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VendorId { get; set; }

        /// <summary>
        /// Gets or sets the USB product ID as a hex string (e.g. "0x7523"), if known.
        /// </summary>
        [JsonPropertyName("productId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ProductId { get; set; }

        /// <summary>
        /// Gets or sets a placeholder RSSI value for cross-transport message compatibility.
        /// </summary>
        [JsonPropertyName("rssi")]
        public int RSSI { get; set; }
    }

    /// <summary>
    /// A single port returned by <see cref="DoEnumeratePorts"/>, pairing the platform
    /// port handle with the display metadata needed to report it.
    /// </summary>
    protected sealed class EnumeratedPort
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EnumeratedPort"/> class.
        /// </summary>
        /// <param name="port">The platform-specific port handle.</param>
        /// <param name="path">The OS-level port path (e.g. "COM7").</param>
        /// <param name="displayName">The user-visible name.</param>
        /// <param name="vendorIdHex">The USB vendor ID as a hex string, or null.</param>
        /// <param name="productIdHex">The USB product ID as a hex string, or null.</param>
        public EnumeratedPort(TPort port, string path, string displayName, string vendorIdHex, string productIdHex)
        {
            this.Port = port;
            this.Path = path;
            this.DisplayName = displayName;
            this.VendorIdHex = vendorIdHex;
            this.ProductIdHex = productIdHex;
        }

        /// <summary>Gets the platform-specific port handle.</summary>
        public TPort Port { get; }

        /// <summary>Gets the OS-level port path.</summary>
        public string Path { get; }

        /// <summary>Gets the user-visible name.</summary>
        public string DisplayName { get; }

        /// <summary>Gets the USB vendor ID as a hex string, or null.</summary>
        public string VendorIdHex { get; }

        /// <summary>Gets the USB product ID as a hex string, or null.</summary>
        public string ProductIdHex { get; }
    }

    /// <summary>
    /// Payload of a <c>listSerialPorts</c> response.
    /// </summary>
    protected class SerialPortListResult
    {
        /// <summary>
        /// Gets or sets the snapshot of currently matching ports.
        /// </summary>
        [JsonPropertyName("ports")]
        public List<SerialPortListItem> Ports { get; set; }
    }

    /// <summary>
    /// A single entry in a <c>listSerialPorts</c> response. Mirrors the
    /// <c>didDiscoverPeripheral</c> fields without the RSSI placeholder.
    /// </summary>
    protected class SerialPortListItem
    {
        /// <summary>
        /// Gets or sets the peripheral ID used by the client to connect.
        /// </summary>
        [JsonPropertyName("peripheralId")]
        public string PeripheralId { get; set; }

        /// <summary>
        /// Gets or sets the user-visible name of the port.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the OS-level port path (e.g. "COM7").
        /// </summary>
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Path { get; set; }

        /// <summary>
        /// Gets or sets the USB vendor ID as a hex string, if known.
        /// </summary>
        [JsonPropertyName("vendorId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VendorId { get; set; }

        /// <summary>
        /// Gets or sets the USB product ID as a hex string, if known.
        /// </summary>
        [JsonPropertyName("productId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ProductId { get; set; }
    }

    private sealed class KeepAliveEntry
    {
        public KeepAliveEntry(string id, int intervalMs, byte[] payload)
        {
            this.Id = id;
            this.IntervalMs = intervalMs;
            this.Payload = payload;
        }

        public string Id { get; }

        public int IntervalMs { get; }

        // Mutable so setKeepAlivePayload can refresh a dynamic packet; guarded by stateLock.
        public byte[] Payload { get; set; }

        // Created by StartKeepAlive, nulled and disposed by StopKeepAlive; guarded by stateLock.
        public Timer Timer { get; set; }
    }
}
