// <copyright file="WinSerialPortInfo.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Win.Serial;

/// <summary>
/// <see cref="WinSerialPortEnumerator"/>가 반환하는 포트 하나.
/// </summary>
internal class WinSerialPortInfo
{
    /// <summary>
    /// OS 수준 포트 경로(예: "COM7")를 가져오거나 설정한다.
    /// </summary>
    public string Path { get; set; }

    /// <summary>
    /// 사용자에게 보이는 이름(보통 COM 번호 포함)을 가져오거나 설정한다.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// USB 벤더 ID를 가져오거나 설정한다. 파싱 불가 시 null.
    /// </summary>
    public int? VendorId { get; set; }

    /// <summary>
    /// USB 제품 ID를 가져오거나 설정한다. 파싱 불가 시 null.
    /// </summary>
    public int? ProductId { get; set; }

    /// <summary>
    /// 원본 PNPDeviceID를 가져오거나 설정한다. 갑작스러운 분리(surprise removal) 매칭에 유용하다.
    /// </summary>
    public string PnpDeviceId { get; set; }
}
