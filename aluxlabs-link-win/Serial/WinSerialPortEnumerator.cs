// <copyright file="WinSerialPortEnumerator.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Win.Serial;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using AluxLabs.Link.Serial;

/// <summary>
/// WMI(Win32_PnPEntity)로 Windows의 USB 시리얼 포트를 열거하며, PNPDeviceID에서
/// COM 포트 이름과 USB VID/PID를 추출한다. <see cref="WinSerialSession"/>이 검색에 사용한다.
/// </summary>
internal static class WinSerialPortEnumerator
{
    private const string WmiQuery =
        "SELECT DeviceID, PNPDeviceID, Caption, Name FROM Win32_PnPEntity " +
        "WHERE PNPClass = 'Ports' AND PNPDeviceID LIKE 'USB%'";

    private static readonly Regex VidPidRegex = new (
        @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ComPortRegex = new (
        @"\((COM\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 주어진 필터 중 하나라도 일치하는 USB 시리얼 포트를 WMI로 동기 조회한다.
    /// </summary>
    /// <param name="filters">필터 목록. 비어 있으면 "일치하는 모든 USB 시리얼 포트 반환"을 뜻한다.</param>
    /// <returns>일치하는 포트 목록. 비어 있을 수 있다.</returns>
    public static IReadOnlyList<WinSerialPortInfo> Query(IReadOnlyList<SerialDiscoveryFilter> filters)
    {
        var results = new List<WinSerialPortInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(WmiQuery);
            using var collection = searcher.Get();

            foreach (var item in collection)
            {
                using var mo = (ManagementObject)item;
                var info = BuildPortInfo(mo);
                if (info == null)
                {
                    continue;
                }

                if (!MatchesAnyFilter(info, filters))
                {
                    continue;
                }

                results.Add(info);
            }
        }
        catch (ManagementException e)
        {
            Trace.WriteLine($"WMI query failed during serial port enumeration: {e}");
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Unexpected error during serial port enumeration: {e}");
        }

        return results;
    }

    private static WinSerialPortInfo BuildPortInfo(ManagementObject mo)
    {
        var pnpId = mo["PNPDeviceID"] as string ?? string.Empty;
        var caption = mo["Caption"] as string ?? string.Empty;
        var name = mo["Name"] as string ?? caption;

        var comMatch = ComPortRegex.Match(caption);
        if (!comMatch.Success)
        {
            // COM 포트 번호가 없으면 우리 관점에선 사용 가능한 시리얼 포트가 아니다.
            return null;
        }

        int? vendorId = null;
        int? productId = null;
        var vidPidMatch = VidPidRegex.Match(pnpId);
        if (vidPidMatch.Success)
        {
            vendorId = int.Parse(vidPidMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            productId = int.Parse(vidPidMatch.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return new WinSerialPortInfo
        {
            Path = comMatch.Groups[1].Value,
            DisplayName = name,
            VendorId = vendorId,
            ProductId = productId,
            PnpDeviceId = pnpId,
        };
    }

    private static bool MatchesAnyFilter(WinSerialPortInfo info, IReadOnlyList<SerialDiscoveryFilter> filters)
    {
        if (filters == null || filters.Count == 0)
        {
            return true;
        }

        foreach (var filter in filters)
        {
            if (filter.UsbVendorId.HasValue && filter.UsbVendorId.Value != info.VendorId)
            {
                continue;
            }

            if (filter.UsbProductId.HasValue && filter.UsbProductId.Value != info.ProductId)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(filter.PathHint))
            {
                if (info.Path == null ||
                    info.Path.IndexOf(filter.PathHint, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
            }

            return true;
        }

        return false;
    }
}
