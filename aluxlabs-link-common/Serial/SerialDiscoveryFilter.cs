// <copyright file="SerialDiscoveryFilter.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Serial;

/// <summary>
/// 클라이언트가 시리얼 "discover" 요청에 담아 보내는 필터 항목 하나.
/// 목록 안의 어느 필터든 일치하면 해당 포트가 보고된다.
/// </summary>
internal class SerialDiscoveryFilter
{
    /// <summary>
    /// 매칭할 USB 벤더 ID를 10진 정수로 가져오거나 설정한다.
    /// </summary>
    public int? UsbVendorId { get; set; }

    /// <summary>
    /// 매칭할 USB 제품 ID를 10진 정수로 가져오거나 설정한다.
    /// </summary>
    public int? UsbProductId { get; set; }

    /// <summary>
    /// 매칭할 포트 경로 부분 문자열(예: "COM7")을 가져오거나 설정한다. 선택 사항이다.
    /// </summary>
    public string PathHint { get; set; }
}
