// <copyright file="KeepAliveEntryParam.cs" company="ALUX">
// Copyright (c) 2026 ALUX, Inc. All rights reserved.
// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.
// </copyright>

namespace AluxLabs.Link.Serial;

/// <summary>
/// One keep-alive entry parsed from a connect request: an opaque periodic packet plus its send cadence.
/// </summary>
internal sealed class KeepAliveEntryParam
{
    /// <summary>
    /// Gets or sets the client-assigned id used to target this entry in a later <c>setKeepAlivePayload</c>.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the bytes written to the serial line on each cadence tick. May be null until first set.
    /// </summary>
    public byte[] Payload { get; set; }

    /// <summary>
    /// Gets or sets the send cadence in milliseconds. Must be positive.
    /// </summary>
    public int IntervalMs { get; set; }
}
