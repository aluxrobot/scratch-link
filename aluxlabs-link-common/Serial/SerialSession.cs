// <copyright file="SerialSession.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Serial;

using System;
using System.Collections.Concurrent;
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
/// USB 시리얼 트랜스포트 세션의 크로스 플랫폼 베이스. 시리얼 전용 알림 이름
/// (<c>serialDidReceiveData</c>, <c>serialDidDisconnect</c>)을 사용해
/// 호출자가 시리얼 이벤트를 BLE 특성 이벤트나 BT 메시지 이벤트와 혼동하지 않게 한다.
/// </summary>
/// <typeparam name="TPort">플랫폼별 포트 핸들. <see cref="DoConnect(TPort, SerialOpenParams)"/>로 다시 전달된다.</typeparam>
internal abstract class SerialSession<TPort> : PeripheralSession<TPort, string>
    where TPort : class
{
    // 두 write가 겹쳐 스트림을 깨뜨리지 않도록 DoWrite 호출을 직렬화한다.
    private readonly SemaphoreSlim writeSemaphore = new SemaphoreSlim(1, 1);

    // 타이머 콜백과 공유하는 keep-alive 수명 주기 필드를 보호한다.
    private readonly object stateLock = new object();

    // 장치가 요구하는 주기 패킷들; 각자 자체 주기 타이머로 돈다. stateLock으로 보호.
    private readonly List<KeepAliveEntry> keepAliveEntries = new ();

    // paced 동안 장치 수신을 기다리는 클라이언트 write와 keep-alive 패킷; FlushTxQueue가 FIFO 순으로 비운다.
    private readonly ConcurrentQueue<byte[]> txQueue = new ();

    private bool keepAliveActive;

    // hot path에서 stateLock 밖 스레드도 최신 값을 보도록 volatile.
    private volatile bool wireTrace;

    // 설정되면 TX가 장치 RX에 게이트된다: write가 즉시 나가지 않고 큐에 쌓인다. connect 시 구성.
    private volatile bool pacedWrite;

    // 첫 장치 RX 후에만 게이팅이 arm되므로, connect 시점 write는 즉시 나가 주소를 받아야만 말하는 장치를 깨운다.
    private volatile bool pacedArmed;

    // 마지막으로 수락(=TX flush 트리거)한 RX의 Stopwatch 틱; 분할 판정 기준. await 이전 동기 구간에서만 접근해 레이스를 피한다.
    private long lastAcceptedRxTs;

    // 분할 패킷 판정용 장치 송신 주기(ms); 직전 수락 RX 후 이 값의 80% 미만에 온 RX는 분할로 보고 flush를 건너뛴다. 0이면 비활성. connect 시 구성.
    private volatile int expectedRxPeriodMs;

    // paced 큐가 비어 있을 때 장치 수신 시 보낸다; null이면 fallback 비활성화. connect 시 구성.
    private volatile byte[] idlePayload;

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
        this.Handlers["setPacedWrite"] = this.HandleSetPacedWrite;
        this.Handlers["triggerDTRReset"] = this.HandleTriggerDTRReset;
    }

    /// <inheritdoc/>
    protected override string GeneratePeripheralId(string peripheralAddress)
    {
        // 시리얼 포트 경로는 안정적이고 민감하지 않으므로, 익명화 GUID 대신 그대로 ID로 쓴다.
        return peripheralAddress;
    }

    /// <summary>
    /// JSON-RPC "discover" 요청을 구현한다. 필터 목록을 파싱하고 플랫폼별 열거를
    /// 시작한다. 발견된 포트는 <see cref="OnPortDiscovered"/>로 스트리밍된다.
    /// </summary>
    /// <param name="methodName">호출되는 메서드 이름("discover").</param>
    /// <param name="args">선택적으로 <c>filters</c> 배열을 담은 JSON 객체.</param>
    /// <returns>빈 결과로 완료되는 <see cref="Task"/>; 발견 결과는 알림으로 스트리밍된다.</returns>
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
    /// JSON-RPC "listSerialPorts" 요청을 구현한다. 현재 일치하는 포트의 일회성 스냅샷을
    /// 응답으로 반환하며(알림 스트리밍 없음), 이후 <c>connect</c>가 peripheral ID를 해석할 수
    /// 있도록 각 포트를 등록한다.
    /// </summary>
    /// <param name="methodName">호출되는 메서드 이름("listSerialPorts").</param>
    /// <param name="args">선택적으로 <c>filters</c> 배열을 담은 JSON 객체.</param>
    /// <returns>일치하는 포트의 <c>ports</c> 배열로 완료되는 <see cref="Task"/>.</returns>
    protected async Task<object> HandleListSerialPorts(string methodName, JsonElement? args)
    {
        var filters = ParseFilters(args);
        Trace.WriteLine($"received listSerialPorts request with {filters.Count} filter(s)");

        // 레지스트리를 갱신해, 이전 스냅샷의 ID는 사라진 포트를 여는 대신 connect 실패(-32600)가 나게 한다.
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
    /// 필터에 일치하는, 현재 존재하는 시리얼 포트를 플랫폼별로 일회성 열거한다.
    /// 한 번의 호출로 완전한 스냅샷을 반환하며 스트리밍하지 않는다.
    /// </summary>
    /// <param name="filters">클라이언트가 보낸 필터 목록. 비어 있으면 "전부 일치"를 뜻한다.</param>
    /// <returns>일치하는 포트로 완료되는 <see cref="Task"/>.</returns>
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

        this.txQueue.Clear();
        this.pacedWrite = openParams.PacedWrite;
        this.pacedArmed = false;
        this.lastAcceptedRxTs = 0;
        this.expectedRxPeriodMs = openParams.ExpectedRxPeriodMs;
        this.idlePayload = openParams.IdlePayload;
        if (this.pacedWrite)
        {
            Trace.WriteLine("paced-write: requested; gating arms on first device RX");
        }

        // 플랫폼 DoConnect가 타이머를 시작하기 전에 구성해, 첫 틱이 이미 항목들을 보게 한다.
        this.ConfigureKeepAlive(openParams.KeepAlive);

        return this.DoConnect(port, openParams);
    }

    /// <summary>
    /// 주어진 포트를 여는 플랫폼별 구현. 성공 시 RX가 활성화되어야 하며,
    /// 들어오는 바이트는 <see cref="DidReceiveData"/>로 보고되어야 한다.
    /// </summary>
    /// <param name="port"><see cref="OnPortDiscovered"/>로 미리 등록된 포트 핸들.</param>
    /// <param name="openParams">connect 요청에서 추출한 open 파라미터.</param>
    /// <returns>비동기 작업을 나타내는 <see cref="Task"/>.</returns>
    protected abstract Task<object> DoConnect(TPort port, SerialOpenParams openParams);

    /// <summary>
    /// JSON-RPC <c>write</c> 핸들러. write 락 하에서 클라이언트 메시지를 시리얼 라인으로 보낸다.
    /// </summary>
    /// <param name="methodName">디스패치된 메서드 이름.</param>
    /// <param name="args">디코딩된 요청 파라미터.</param>
    /// <returns><c>sentBytes</c> 래퍼.</returns>
    protected async Task<object> HandleWrite(string methodName, JsonElement? args)
    {
        if (args == null)
        {
            throw JsonRpc2Error.InvalidParams("write requires a message buffer").ToException();
        }

        var buffer = EncodingHelpers.DecodeBuffer(args.Value);

        if (this.pacedWrite && this.pacedArmed)
        {
            if (this.wireTrace)
            {
                Trace.WriteLine($"wire-trace TX(queued) {buffer.Length}B {FormatHex(buffer)}");
            }

            this.txQueue.Enqueue(buffer);
            return new Dictionary<string, int> { ["sentBytes"] = buffer.Length };
        }

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
    /// 포트로 바이트를 보내는 플랫폼별 구현.
    /// </summary>
    /// <param name="data">보낼 바이트.</param>
    /// <returns>실제로 기록된 바이트 수.</returns>
    protected abstract Task<int> DoWrite(byte[] data);

    /// <summary>
    /// JSON-RPC "disconnect" 요청을 구현한다. <c>serialDidDisconnect</c> 알림을 내보내지 않고
    /// 포트를 닫는다(그 알림은 외부 원인 disconnect 전용이다).
    /// </summary>
    /// <param name="methodName">호출되는 메서드 이름("disconnect").</param>
    /// <param name="args">사용하지 않음.</param>
    /// <returns>빈 결과.</returns>
    protected async Task<object> HandleDisconnect(string methodName, JsonElement? args)
    {
        await this.DoDisconnect();
        return new Dictionary<string, object>();
    }

    /// <summary>
    /// 포트를 닫는 플랫폼별 구현.
    /// </summary>
    /// <returns>비동기 작업을 나타내는 <see cref="Task"/>.</returns>
    protected abstract Task DoDisconnect();

    /// <summary>
    /// JSON-RPC "startReading" 요청을 구현한다. RX는 connect 시 자동으로 켜지므로
    /// 기본 구현은 no-op이다. 서브클래스는 <c>stopReading</c> 이후 RX를 다시 켜기 위해
    /// 오버라이드할 수 있다.
    /// </summary>
    /// <param name="methodName">호출되는 메서드 이름("startReading").</param>
    /// <param name="args">사용하지 않음.</param>
    /// <returns>빈 결과.</returns>
    protected virtual Task<object> HandleStartReading(string methodName, JsonElement? args)
    {
        return Task.FromResult<object>(new Dictionary<string, object>());
    }

    /// <summary>
    /// JSON-RPC "stopReading" 요청을 구현한다. 기본은 no-op이다.
    /// </summary>
    /// <param name="methodName">호출되는 메서드 이름("stopReading").</param>
    /// <param name="args">사용하지 않음.</param>
    /// <returns>빈 결과.</returns>
    protected virtual Task<object> HandleStopReading(string methodName, JsonElement? args)
    {
        return Task.FromResult<object>(new Dictionary<string, object>());
    }

    /// <summary>
    /// JSON-RPC <c>setKeepAlive</c> 핸들러. keep-alive 타이머 집합 전체를 켜거나 끈다:
    /// 양수 <c>intervalMs</c>는 구성된 모든 항목을 각자 주기로 재개하고, null/0/음수는 전부 일시정지한다.
    /// 멱등(stop 후 start)하다. 응답은 적용된 값을 echo한다(정지 시 null).
    /// </summary>
    /// <param name="methodName">디스패치된 메서드 이름.</param>
    /// <param name="args">디코딩된 요청 파라미터.</param>
    /// <returns>적용된 값의 echo.</returns>
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

        // stop 후 start로, 현재 상태와 무관하게 호출을 멱등으로 만든다.
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
    /// JSON-RPC <c>setKeepAlivePayload</c> 핸들러. 동적 패킷이 최신을 유지하도록 구성된 항목 하나의 페이로드를
    /// 런타임에 교체한다; 주기는 그대로이고 다음 틱이 새 바이트를 보낸다. 알 수 없는 id면 모든 항목을 건드리지 않고
    /// <c>applied: false</c>를 보고한다.
    /// </summary>
    /// <param name="methodName">디스패치된 메서드 이름.</param>
    /// <param name="args">디코딩된 요청 파라미터.</param>
    /// <returns>id와 구성된 항목에 일치했는지 여부의 echo.</returns>
    protected Task<object> HandleSetKeepAlivePayload(string methodName, JsonElement? args)
    {
        var id = args?.TryGetProperty("id")?.GetString();
        if (string.IsNullOrEmpty(id))
        {
            throw JsonRpc2Error.InvalidParams("setKeepAlivePayload requires an id").ToException();
        }

        var payload = DecodeBase64Payload(args?.TryGetProperty("payload")?.GetString());

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

        return Task.FromResult<object>(new Dictionary<string, object> { ["id"] = id, ["applied"] = applied });
    }

    /// <summary>
    /// JSON-RPC <c>setPacedWrite</c> 핸들러. 장치-RX 게이트 write 큐잉을 런타임에 켜거나 끈다. raw 요청-응답
    /// 시퀀스(예: 부트로더 펌웨어 업데이트) 전에는 꺼서 write가 버퍼링 없이 즉시 나가게 한다. 어느 쪽으로 토글하든
    /// 큐에 쌓인 패킷은 폐기된다. 응답은 적용된 상태를 echo한다.
    /// </summary>
    /// <param name="methodName">디스패치된 메서드 이름.</param>
    /// <param name="args">디코딩된 요청 파라미터.</param>
    /// <returns>적용된 상태의 echo.</returns>
    protected Task<object> HandleSetPacedWrite(string methodName, JsonElement? args)
    {
        var enabled = args?.TryGetProperty("enabled")?.GetBoolean();
        if (enabled == null)
        {
            throw JsonRpc2Error.InvalidParams("setPacedWrite requires an 'enabled' boolean").ToException();
        }

        this.pacedWrite = enabled.Value;
        this.pacedArmed = false;
        this.lastAcceptedRxTs = 0;
        this.txQueue.Clear();

        Trace.WriteLine($"paced-write: setPacedWrite enabled={enabled.Value}");
        return Task.FromResult<object>(new Dictionary<string, object> { ["enabled"] = enabled.Value });
    }

    /// <summary>
    /// JSON-RPC <c>triggerDTRReset</c> 핸들러. write가 DTR 펄스 시퀀스와 겹치지 않도록 write 세마포어를
    /// 획득한 뒤 <see cref="DoTriggerDTRReset"/>에 위임한다.
    /// </summary>
    /// <param name="methodName">디스패치된 메서드 이름.</param>
    /// <param name="args">사용하지 않음.</param>
    /// <returns>DTR 펄스 시퀀스 완료 후 반환되는 빈 결과.</returns>
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
    /// DTR 리셋 펄스의 플랫폼별 구현. DTR을 50ms 동안 assert한 뒤 해제한다. 실패 시
    /// 호출자가 올바른 형식의 JSON-RPC 에러 응답을 받도록 <see cref="JsonRpc2Exception"/>을 던져야 한다.
    /// </summary>
    /// <returns>비동기 작업을 나타내는 <see cref="Task"/>.</returns>
    protected abstract Task DoTriggerDTRReset();

    /// <summary>
    /// 수신한 바이트를 <c>serialDidReceiveData</c> 알림으로 클라이언트에 보고한다.
    /// 페이로드는 base64로 인코딩된다.
    /// </summary>
    /// <param name="data">수신한 바이트.</param>
    /// <returns>비동기 작업을 나타내는 <see cref="Task"/>.</returns>
    protected async Task DidReceiveData(byte[] data)
    {
        var rxAt = Stopwatch.GetTimestamp();
        if (this.wireTrace)
        {
            Trace.WriteLine($"wire-trace RX {data.Length}B {FormatHex(data)}");
        }

        if (this.pacedWrite && !this.pacedArmed)
        {
            this.pacedArmed = true;
            Trace.WriteLine("paced-write: armed on first device RX");
        }

        // 분할 판정·기준 갱신을 await 이전(동기 구간)에서 끝낸다. RX 보고는 순차적이라 근접한 두 RX가 같은 기준으로 경쟁하지 않는다.
        var flush = this.pacedWrite && this.pacedArmed;
        if (flush)
        {
            var period = this.expectedRxPeriodMs;
            var lastAccepted = this.lastAcceptedRxTs;
            if (period > 0 && lastAccepted != 0 &&
                (rxAt - lastAccepted) / (Stopwatch.Frequency / 1000.0) < period * 0.8)
            {
                flush = false;
            }
            else
            {
                this.lastAcceptedRxTs = rxAt;
            }
        }

        var encoded = EncodingHelpers.EncodeBuffer(data, "base64");

        await this.SendNotification("serialDidReceiveData", new SerialDataReceived
        {
            Encoding = "base64",
            Message = encoded,
        });

        // 브라우저 RX 알림을 먼저 보낸 뒤 큐를 flush한다; flush를 앞에 두면 매 RX마다 브라우저 알림이 송신 한 번만큼 밀린다.
        if (flush)
        {
            await this.FlushTxQueue();
        }
    }

    /// <summary>
    /// 외부 원인 disconnect를 <c>serialDidDisconnect</c> 알림으로 클라이언트에 보고한다.
    /// 클라이언트가 시작한 <c>disconnect</c> 요청에는 발화하지 않는다.
    /// </summary>
    /// <param name="reason">"user", "device", "error", "shutdown" 중 하나.</param>
    /// <param name="message">선택적인 사람이 읽을 수 있는 상세.</param>
    /// <returns>비동기 작업을 나타내는 <see cref="Task"/>.</returns>
    protected async Task DidDisconnect(string reason, string message = null)
    {
        await this.SendNotification("serialDidDisconnect", new SerialDisconnectMessage
        {
            Reason = reason,
            Message = message,
        });
    }

    /// <summary>
    /// 발견한 포트를 추적하고 클라이언트에 보고한다.
    /// <see cref="PeripheralSession{TPort, String}.RegisterPeripheral"/>로
    /// 세션 범위 peripheral ID를 얻는다.
    /// </summary>
    /// <param name="port">플랫폼별 포트 핸들.</param>
    /// <param name="path">주소로 쓰이는 OS 수준 포트 경로(예: "COM7").</param>
    /// <param name="displayName">사용자에게 보이는 이름. 경로를 포함할 수 있다.</param>
    /// <param name="vendorIdHex">16진 문자열 벤더 ID(예: "0x1A86"), 또는 null.</param>
    /// <param name="productIdHex">16진 문자열 제품 ID(예: "0x7523"), 또는 null.</param>
    /// <returns>비동기 작업을 나타내는 <see cref="Task"/>.</returns>
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
    /// 구성된 keep-alive 항목 집합을 교체한다. 타이머를 시작하지 않으며, 포트가 열리면 플랫폼 계층이
    /// <see cref="StartKeepAlive"/>를 호출한다.
    /// </summary>
    /// <param name="entries">장치가 요구하는 주기 패킷들. null/빈 목록이면 keep-alive를 비활성화한다.</param>
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
    /// 구성된 모든 항목에 대해 주기 타이머를 시작한다. 이미 실행 중이거나 구성된 게 없으면 no-op.
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
    /// 모든 항목의 타이머를 정지·해제하며, in-flight 틱이 끝날 때까지 블록한다. 이후 <see cref="StartKeepAlive"/>가
    /// 재개할 수 있도록 구성된 항목은 유지한다. 반복 호출해도 안전하다.
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
            // 후속 disconnect나 포트 해제와 send가 레이스하지 않도록 블록한다.
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
            // 순서 중요: StopKeepAlive가 in-flight 틱을 기다린 뒤에 그 틱이 쓰는 세마포어를 해제한다.
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
            PacedWrite = args?.TryGetProperty("pacedWrite")?.GetBoolean() ?? false,
            IdlePayload = DecodeBase64Payload(args?.TryGetProperty("idlePayload")?.GetString()),
            ExpectedRxPeriodMs = args?.TryGetProperty("expectedRxPeriodMs")?.GetInt32() ?? 0,
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
                Payload = DecodeBase64Payload(item.TryGetProperty("payload")?.GetString()),
            });
        }

        return result;
    }

    private static byte[] DecodeBase64Payload(string base64)
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
            throw JsonRpc2Error.InvalidParams("payload must be base64-encoded").ToException();
        }
    }

    /// <summary>
    /// 진단 로그용 16진 미리보기. <paramref name="maxBytes"/>에서 잘리고 꼬리 마커가 붙는다.
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

    private async Task FlushTxQueue()
    {
        await this.writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var sentCount = 0;
            while (this.txQueue.TryDequeue(out var packet))
            {
                sentCount++;
                if (this.wireTrace)
                {
                    Trace.WriteLine($"wire-trace TX(flush) {packet.Length}B {FormatHex(packet)}");
                }

                await this.DoWrite(packet).ConfigureAwait(false);
            }

            var idle = this.idlePayload;
            if (sentCount == 0 && idle != null && idle.Length > 0)
            {
                if (this.wireTrace)
                {
                    Trace.WriteLine($"wire-trace TX(idle) {idle.Length}B {FormatHex(idle)}");
                }

                await this.DoWrite(idle).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // DidReceiveData는 fire-and-forget이라 빠져나간 예외가 관측되지 않는다; 로그 남기고 flush를 멈춘다.
            Trace.WriteLine($"paced flush failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try
            {
                this.writeSemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // 종료 중 세마포어가 해제됨.
            }
        }
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

        // arm되면 장치 읽기가 모든 TX를 게이트하므로, 주기 패킷도 지금 나가지 않고 큐에 합류한다.
        if (this.pacedWrite && this.pacedArmed)
        {
            this.txQueue.Enqueue(data);
            return;
        }

        // WaitAsync(0)으로 틱을 idle 전용으로 만든다: write 버스트 중엔 세마포어가 바빠서 no-op.
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
            // 틱 도중 세션이 해제됨.
        }
        catch (Exception e)
        {
            // async-void Timer 콜백: 빠져나간 예외는 프로세스를 종료시키므로, 로그 남기고 넘어간다.
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
                // 종료 중 세마포어가 해제됨.
            }
        }
    }

    /// <summary>
    /// <c>serialDidReceiveData</c> 알림의 페이로드.
    /// </summary>
    protected class SerialDataReceived
    {
        /// <summary>
        /// 인코딩 식별자를 가져오거나 설정한다; 시리얼 RX는 항상 "base64".
        /// </summary>
        [JsonPropertyName("encoding")]
        public string Encoding { get; set; }

        /// <summary>
        /// 인코딩된 페이로드를 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    /// <summary>
    /// <c>serialDidDisconnect</c> 알림의 페이로드.
    /// </summary>
    protected class SerialDisconnectMessage
    {
        /// <summary>
        /// disconnect 원인을 가져오거나 설정한다: "user", "device", "error", "shutdown".
        /// </summary>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Reason { get; set; }

        /// <summary>
        /// 선택적인 사람이 읽을 수 있는 상세 메시지를 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Message { get; set; }
    }

    /// <summary>
    /// 시리얼 트랜스포트의 <c>didDiscoverPeripheral</c> 알림 페이로드.
    /// </summary>
    protected class SerialPortDiscovered
    {
        /// <summary>
        /// 클라이언트가 연결에 사용하는 세션 범위 peripheral ID를 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("peripheralId")]
        public string PeripheralId { get; set; }

        /// <summary>
        /// 포트의 사용자에게 보이는 이름을 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// OS 수준 포트 경로(예: "COM7")를 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("path")]
        public string Path { get; set; }

        /// <summary>
        /// USB 벤더 ID를 16진 문자열(예: "0x1A86")로, 알려진 경우 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("vendorId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VendorId { get; set; }

        /// <summary>
        /// USB 제품 ID를 16진 문자열(예: "0x7523")로, 알려진 경우 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("productId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ProductId { get; set; }

        /// <summary>
        /// 트랜스포트 간 메시지 호환을 위한 플레이스홀더 RSSI 값을 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("rssi")]
        public int RSSI { get; set; }
    }

    /// <summary>
    /// <see cref="DoEnumeratePorts"/>가 반환하는 포트 하나. 플랫폼 포트 핸들과
    /// 보고에 필요한 표시 메타데이터를 묶는다.
    /// </summary>
    protected sealed class EnumeratedPort
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EnumeratedPort"/> class.
        /// </summary>
        /// <param name="port">플랫폼별 포트 핸들.</param>
        /// <param name="path">OS 수준 포트 경로(예: "COM7").</param>
        /// <param name="displayName">사용자에게 보이는 이름.</param>
        /// <param name="vendorIdHex">16진 문자열 USB 벤더 ID, 또는 null.</param>
        /// <param name="productIdHex">16진 문자열 USB 제품 ID, 또는 null.</param>
        public EnumeratedPort(TPort port, string path, string displayName, string vendorIdHex, string productIdHex)
        {
            this.Port = port;
            this.Path = path;
            this.DisplayName = displayName;
            this.VendorIdHex = vendorIdHex;
            this.ProductIdHex = productIdHex;
        }

        /// <summary>플랫폼별 포트 핸들을 가져온다.</summary>
        public TPort Port { get; }

        /// <summary>OS 수준 포트 경로를 가져온다.</summary>
        public string Path { get; }

        /// <summary>사용자에게 보이는 이름을 가져온다.</summary>
        public string DisplayName { get; }

        /// <summary>16진 문자열 USB 벤더 ID, 또는 null을 가져온다.</summary>
        public string VendorIdHex { get; }

        /// <summary>16진 문자열 USB 제품 ID, 또는 null을 가져온다.</summary>
        public string ProductIdHex { get; }
    }

    /// <summary>
    /// <c>listSerialPorts</c> 응답의 페이로드.
    /// </summary>
    protected class SerialPortListResult
    {
        /// <summary>
        /// 현재 일치하는 포트의 스냅샷을 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("ports")]
        public List<SerialPortListItem> Ports { get; set; }
    }

    /// <summary>
    /// <c>listSerialPorts</c> 응답의 항목 하나. RSSI 플레이스홀더를 뺀
    /// <c>didDiscoverPeripheral</c> 필드를 그대로 반영한다.
    /// </summary>
    protected class SerialPortListItem
    {
        /// <summary>
        /// 클라이언트가 연결에 사용하는 peripheral ID를 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("peripheralId")]
        public string PeripheralId { get; set; }

        /// <summary>
        /// 포트의 사용자에게 보이는 이름을 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// OS 수준 포트 경로(예: "COM7")를 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Path { get; set; }

        /// <summary>
        /// USB 벤더 ID를 16진 문자열로, 알려진 경우 가져오거나 설정한다.
        /// </summary>
        [JsonPropertyName("vendorId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string VendorId { get; set; }

        /// <summary>
        /// USB 제품 ID를 16진 문자열로, 알려진 경우 가져오거나 설정한다.
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

        // setKeepAlivePayload가 동적 패킷을 갱신할 수 있도록 가변; stateLock으로 보호.
        public byte[] Payload { get; set; }

        // StartKeepAlive가 생성하고 StopKeepAlive가 null 처리·해제; stateLock으로 보호.
        public Timer Timer { get; set; }
    }
}
