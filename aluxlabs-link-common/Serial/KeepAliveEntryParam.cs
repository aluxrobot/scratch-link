// <copyright file="KeepAliveEntryParam.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Serial;

/// <summary>
/// connect 요청에서 파싱된 keep-alive 항목 하나. 불투명한 주기 패킷과 그 송신 주기로 구성된다.
/// </summary>
internal sealed class KeepAliveEntryParam
{
    /// <summary>
    /// 이후 <c>setKeepAlivePayload</c>에서 이 항목을 지정할 때 쓰는, 클라이언트가 부여한 id를 가져오거나 설정한다.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// 매 주기 틱마다 시리얼 라인에 기록되는 바이트를 가져오거나 설정한다. 최초 설정 전까지는 null일 수 있다.
    /// </summary>
    public byte[] Payload { get; set; }

    /// <summary>
    /// 송신 주기(밀리초)를 가져오거나 설정한다. 양수여야 한다.
    /// </summary>
    public int IntervalMs { get; set; }
}
