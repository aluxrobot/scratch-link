// <copyright file="WinSerialSession.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Win.Serial;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Fleck;
using AluxLabs.Link.JsonRpc;
using AluxLabs.Link.Serial;

/// <summary>
/// Windows에서 I/O는 <see cref="SerialPort"/>로, VID/PID 기반 포트 검색은 WMI로 처리하는
/// USB 시리얼 세션 구현.
/// </summary>
internal class WinSerialSession : SerialSession<WinSerialPortInfo>
{
    // 같은 핸들에서 Read와 Write를 직렬화한다: CH340/CP210x 드라이버에선 동시 호출 시 read 쪽에 TimeoutException이 폭주한다.
    private readonly object ioLock = new object();

    private SerialPort port;
    private CancellationTokenSource rxCts;
    private Task rxLoop;
    private ManagementEventWatcher removalWatcher;
    private string connectedPnpDeviceId;
    private int disconnectNotified;
    private bool timerRaised;

    /// <summary>
    /// Initializes a new instance of the <see cref="WinSerialSession"/> class.
    /// </summary>
    /// <param name="webSocket">이 세션의 WebSocket 연결.</param>
    public WinSerialSession(IWebSocketConnection webSocket)
        : base(webSocket)
    {
    }

    /// <inheritdoc/>
    protected override bool IsConnected => this.port != null && this.port.IsOpen;

    /// <inheritdoc/>
    protected override async Task<IReadOnlyList<EnumeratedPort>> DoEnumeratePorts(IReadOnlyList<SerialDiscoveryFilter> filters)
    {
        var ports = await Task.Run(() => WinSerialPortEnumerator.Query(filters));

        var result = new List<EnumeratedPort>(ports.Count);
        foreach (var portInfo in ports)
        {
            var vendorHex = portInfo.VendorId.HasValue
                ? $"0x{portInfo.VendorId.Value:X4}"
                : null;
            var productHex = portInfo.ProductId.HasValue
                ? $"0x{portInfo.ProductId.Value:X4}"
                : null;

            result.Add(new EnumeratedPort(portInfo, portInfo.Path, portInfo.DisplayName, vendorHex, productHex));
        }

        return result;
    }

    /// <inheritdoc/>
    protected override Task<object> DoConnect(WinSerialPortInfo info, SerialOpenParams openParams)
    {
        if (this.port != null)
        {
            throw JsonRpc2Error.InvalidRequest("already connected").ToException();
        }

        if (!string.IsNullOrEmpty(openParams.PeripheralType))
        {
            Trace.WriteLine($"Connecting to {info.Path} with peripheral type: {openParams.PeripheralType}");
        }

        try
        {
            this.port = new SerialPort(info.Path)
            {
                BaudRate = openParams.BaudRate,
                DataBits = openParams.DataBits,
                Parity = MapParity(openParams.Parity),
                StopBits = MapStopBits(openParams.StopBits),
                Handshake = MapFlowControl(openParams.FlowControl),
                ReadTimeout = 500,
                WriteTimeout = SerialPort.InfiniteTimeout,
                // CH340 + codetinker 펌웨어는 DTR/RTS 전이를 리셋 신호로 취급한다;
                // SerialPort.Open이 이를 토글하지 않도록 명시적으로 low로 고정한다.
                DtrEnable = false,
                RtsEnable = false,
            };
            this.port.Open();
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Failed to open serial port {info.Path}: {e}");
            this.CloseConnectionSilently();
            throw JsonRpc2Error.ApplicationError($"could not open serial port {info.Path}: {e.Message}").ToException();
        }

        Interlocked.Exchange(ref this.disconnectNotified, 0);

        // RX 폴링의 WaitOne(1)이 1ms로 동작하도록 연결 동안만 시스템 타이머 분해능을 1ms로 올린다.
        NativeMethods.TimeBeginPeriod(1);
        this.timerRaised = true;

        this.rxCts = new CancellationTokenSource();
        var token = this.rxCts.Token;
        this.rxLoop = Task.Run(() => this.ReadLoop(token));

        this.StartKeepAlive();
        this.StartRemovalWatcher(info.PnpDeviceId);

        return Task.FromResult<object>(new Dictionary<string, object>());
    }

    /// <inheritdoc/>
    protected override async Task<int> DoWrite(byte[] data)
    {
        var currentPort = this.port;
        if (currentPort == null || !currentPort.IsOpen)
        {
            throw JsonRpc2Error.InvalidRequest("cannot write when not connected").ToException();
        }

        // ioLock 하에서 동기 Write; 이유는 ioLock 선언부 참조. Task.Run으로 async 시그니처를 디스패처 스레드에서 떼어 둔다.
        try
        {
            await Task.Run(() =>
            {
                lock (this.ioLock)
                {
                    if (!currentPort.IsOpen)
                    {
                        throw new InvalidOperationException("port closed");
                    }

                    currentPort.Write(data, 0, data.Length);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw JsonRpc2Error.InternalError("write failed: port was disposed").ToException();
        }
        catch (InvalidOperationException)
        {
            // 위의 IsOpen 확인과 Write 사이에 포트가 닫혔다.
            throw JsonRpc2Error.InvalidRequest("cannot write when not connected").ToException();
        }
        catch (IOException e)
        {
            // write 중 IOException은 장치가 사라졌다는 뜻; 분리를 빨리 드러내려 idle keep-alive 재전송으로 에스컬레이트한다.
            this.HandleSurpriseRemoval("device", e.Message);
            throw JsonRpc2Error.InternalError($"write failed: {e.Message}").ToException();
        }

        return data.Length;
    }

    /// <inheritdoc/>
    protected override async Task DoDisconnect()
    {
        // 먼저 notified 표시: 클라이언트가 시작한 disconnect는 분리가 레이스로 끼어들어도 serialDidDisconnect를 내보내면 안 된다.
        Interlocked.Exchange(ref this.disconnectNotified, 1);
        this.StopKeepAlive();
        this.StopRemovalWatcher();
        var loop = this.rxLoop;
        this.CloseConnectionSilently();

        if (loop != null)
        {
            try
            {
                await loop;
            }
            catch
            {
                // 무시: 루프 자체 에러 경로가 클라이언트에 보일 내용은 이미 보고했다
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task DoTriggerDTRReset()
    {
        var currentPort = this.port;
        if (currentPort == null || !currentPort.IsOpen)
        {
            throw JsonRpc2Error.InternalError("No connected peripheral").ToException();
        }

        try
        {
            currentPort.DtrEnable = true;
            await Task.Delay(50).ConfigureAwait(false);
            currentPort.DtrEnable = false;
        }
        catch (Exception e) when (e is ObjectDisposedException || e is InvalidOperationException || e is IOException)
        {
            throw JsonRpc2Error.InternalError($"setSignals failed: {e.Message}").ToException();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        this.StopRemovalWatcher();
        this.CloseConnectionSilently();
    }

    private static Parity MapParity(string parity) =>
        (parity ?? "none").ToLowerInvariant() switch
        {
            "none" => Parity.None,
            "even" => Parity.Even,
            "odd" => Parity.Odd,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => throw JsonRpc2Error.InvalidParams($"unsupported parity: {parity}").ToException(),
        };

    private static StopBits MapStopBits(string stopBits) =>
        (stopBits ?? "one").ToLowerInvariant() switch
        {
            "one" => StopBits.One,
            "onepointfive" => StopBits.OnePointFive,
            "two" => StopBits.Two,
            _ => throw JsonRpc2Error.InvalidParams($"unsupported stopBits: {stopBits}").ToException(),
        };

    private static Handshake MapFlowControl(string flow) =>
        (flow ?? "none").ToLowerInvariant() switch
        {
            "none" => Handshake.None,
            "rtscts" => Handshake.RequestToSend,
            "xonxoff" => Handshake.XOnXOff,
            _ => throw JsonRpc2Error.InvalidParams($"unsupported flowControl: {flow}").ToException(),
        };

    private void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            var currentPort = this.port;
            if (currentPort == null || !currentPort.IsOpen)
            {
                break;
            }

            // BytesToRead를 폴링해 데이터가 있을 때만 Read를 호출한다 — ioLock 점유 시간을 최소화한다.
            int available;
            try
            {
                available = currentPort.BytesToRead;
            }
            catch (ObjectDisposedException)
            {
                // InvalidOperationException에서 파생되므로 먼저 catch한다.
                break;
            }
            catch (InvalidOperationException)
            {
                // 취소 요청 없는 "Port closed"는 우리 teardown이 아니라 외부 종료(갑작스러운 분리)를 뜻한다.
                if (!ct.IsCancellationRequested)
                {
                    this.HandleSurpriseRemoval("device", "serial port closed unexpectedly");
                }

                break;
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException e)
            {
                Trace.WriteLine($"Serial BytesToRead IOException on {currentPort.PortName}: {e.Message}");
                this.HandleSurpriseRemoval("device", e.Message);
                break;
            }

            if (available <= 0)
            {
                // ct.WaitHandle에서 대기해 취소 시 루프가 즉시 깨어나게 한다; 그 외엔 RX 인지 지연을 낮추려 1ms만 잔다.
                try
                {
                    if (ct.WaitHandle.WaitOne(1))
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                continue;
            }

            int n;
            try
            {
                lock (this.ioLock)
                {
                    if (!currentPort.IsOpen)
                    {
                        break;
                    }

                    n = currentPort.Read(buf, 0, Math.Min(available, buf.Length));
                }
            }
            catch (TimeoutException)
            {
                // 방어적 처리: BytesToRead 게이트가 이를 막아야 한다.
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException e)
            {
                Trace.WriteLine($"Serial read IOException on {currentPort.PortName}: {e.Message}");
                this.HandleSurpriseRemoval("device", e.Message);
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                if (!ct.IsCancellationRequested)
                {
                    this.HandleSurpriseRemoval("device", "serial port closed unexpectedly");
                }

                break;
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Unexpected serial read error: {e}");
                this.HandleSurpriseRemoval("error", e.Message);
                break;
            }

            if (n <= 0)
            {
                continue;
            }

            var data = new byte[n];
            Buffer.BlockCopy(buf, 0, data, 0, n);
            _ = this.DidReceiveData(data);
        }
    }

    private void CloseConnectionSilently()
    {
        if (this.timerRaised)
        {
            NativeMethods.TimeEndPeriod(1);
            this.timerRaised = false;
        }

        var localCts = this.rxCts;
        this.rxCts = null;

        try
        {
            localCts?.Cancel();
        }
        catch
        {
            // 무시
        }

        var localPort = this.port;
        this.port = null;

        if (localPort != null)
        {
            try
            {
                if (localPort.IsOpen)
                {
                    localPort.Close();
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Error closing serial port: {e}");
            }

            try
            {
                localPort.Dispose();
            }
            catch
            {
                // 무시
            }
        }

        try
        {
            localCts?.Dispose();
        }
        catch
        {
            // 무시
        }
    }

    private void StartRemovalWatcher(string pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId))
        {
            return;
        }

        try
        {
            this.connectedPnpDeviceId = pnpDeviceId;
            var query = new WqlEventQuery(
                "__InstanceDeletionEvent",
                TimeSpan.FromSeconds(1),
                "TargetInstance ISA 'Win32_PnPEntity'");
            this.removalWatcher = new ManagementEventWatcher(query);
            this.removalWatcher.EventArrived += this.OnDeviceRemoved;
            this.removalWatcher.Start();
        }
        catch (Exception e)
        {
            // 백업 감지 수단으로 read 루프 예외와 keep-alive write 실패가 남아 있다.
            Trace.WriteLine($"Failed to start USB removal watcher for {pnpDeviceId}: {e.Message}");
        }
    }

    private void OnDeviceRemoved(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var target = e.NewEvent?["TargetInstance"] as ManagementBaseObject;
            var removedId = target?["PNPDeviceID"] as string;
            if (!this.IsConnectedDevice(removedId))
            {
                return;
            }

            Trace.WriteLine($"USB surprise removal detected: {removedId}");
            this.HandleSurpriseRemoval("device", "USB device removed");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error handling device removal event: {ex.Message}");
        }
    }

    private bool IsConnectedDevice(string removedPnpId)
    {
        var mine = this.connectedPnpDeviceId;
        if (string.IsNullOrEmpty(mine) || string.IsNullOrEmpty(removedPnpId))
        {
            return false;
        }

        // 정확히 그 노드이거나, 우리 장치가 제거된 상위(허브) 노드의 자식인 경우. VID/PID만으로 넓게 매칭하지 않는다.
        return mine.Equals(removedPnpId, StringComparison.OrdinalIgnoreCase)
            || mine.StartsWith(removedPnpId + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleSurpriseRemoval(string reason, string message)
    {
        if (Interlocked.Exchange(ref this.disconnectNotified, 1) != 0)
        {
            return;
        }

        _ = this.DidDisconnect(reason, message);
        this.CloseConnectionSilently();

        // WMI 콜백 스레드 밖에서 Stop: ManagementEventWatcher.Stop을 EventArrived에서 호출하면 데드락날 수 있다.
        _ = Task.Run(() => this.StopRemovalWatcher());
    }

    private void StopRemovalWatcher()
    {
        var watcher = Interlocked.Exchange(ref this.removalWatcher, null);
        this.connectedPnpDeviceId = null;

        if (watcher == null)
        {
            return;
        }

        try
        {
            watcher.EventArrived -= this.OnDeviceRemoved;
            watcher.Stop();
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Error stopping USB removal watcher: {e.Message}");
        }

        try
        {
            watcher.Dispose();
        }
        catch
        {
            // 무시
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        internal static extern uint TimeBeginPeriod(uint uMilliseconds);

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        internal static extern uint TimeEndPeriod(uint uMilliseconds);
    }
}
