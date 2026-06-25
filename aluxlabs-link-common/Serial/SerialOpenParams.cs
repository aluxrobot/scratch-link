// <copyright file="SerialOpenParams.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Serial;

using System.Collections.Generic;

/// <summary>
/// 시리얼 "connect" 요청에서 추출한 파라미터.
/// </summary>
internal class SerialOpenParams
{
    /// <summary>
    /// 보드레이트를 가져오거나 설정한다. 필수.
    /// </summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// 데이터 비트를 가져오거나 설정한다. 기본값 8.
    /// </summary>
    public int DataBits { get; set; }

    /// <summary>
    /// 패리티 설정("none", "even", "odd")을 가져오거나 설정한다. 기본값 "none".
    /// </summary>
    public string Parity { get; set; }

    /// <summary>
    /// 스톱 비트("one", "onePointFive", "two")를 가져오거나 설정한다. 기본값 "one".
    /// </summary>
    public string StopBits { get; set; }

    /// <summary>
    /// 흐름 제어("none", "rtsCts", "xonXoff")를 가져오거나 설정한다. 기본값 "none".
    /// </summary>
    public string FlowControl { get; set; }

    /// <summary>
    /// 클라이언트가 지정한 주변기기 타입 식별자를 가져오거나 설정한다. 선택 사항이며 진단 로깅에만 쓰인다.
    /// </summary>
    public string PeripheralType { get; set; }

    /// <summary>
    /// 장치가 요구하는 keep-alive 항목을 가져오거나 설정한다. 각 항목은 클라이언트 write와 무관하게
    /// 자체 주기로 송신되는 불투명한 패킷이다. 빈 목록이나 null이면 keep-alive를 비활성화한다. 페이로드는 그대로 기록된다.
    /// </summary>
    public IReadOnlyList<KeepAliveEntryParam> KeepAlive { get; set; }

    /// <summary>
    /// write를 즉시 송신하지 않고 버퍼에 모았다가 장치가 데이터를 보낼 때만 라인으로 flush할지 여부를
    /// 가져오거나 설정한다. 기본값은 꺼짐.
    /// </summary>
    public bool PacedWrite { get; set; }

    /// <summary>
    /// paced-write 큐가 비어 있을 때 장치 수신 시점에 보낼 패킷을 가져오거나 설정한다. null이면 fallback을 비활성화한다.
    /// </summary>
    public byte[] IdlePayload { get; set; }

    /// <summary>
    /// 펌웨어의 예상 송신 주기(ms)를 가져오거나 설정한다. paced 모드에서 직전 flush 후 이 값의 80% 미만에 도착한
    /// 장치 수신은 분할 패킷으로 간주해 TX flush를 건너뛴다. 0이면 분할 감지를 비활성화한다.
    /// </summary>
    public int ExpectedRxPeriodMs { get; set; }

    /// <summary>
    /// 와이어 레벨 TX/RX 16진 덤프를 <see cref="System.Diagnostics.Trace"/>로 출력할지 여부를
    /// 가져오거나 설정한다. 진단용이며 기본값은 꺼짐.
    /// </summary>
    public bool WireTrace { get; set; }
}
