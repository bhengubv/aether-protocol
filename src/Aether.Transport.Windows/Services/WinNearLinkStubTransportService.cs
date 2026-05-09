// SPDX-License-Identifier: MIT

using Aether.Transport.NearLink;

namespace Aether.Transport.Windows.Services;

/// <summary>
/// Windows stub for the NearLink (Aether Teal) transport.
///
/// NearLink is a Huawei proprietary short-range protocol (successor to BLE/Wi-Fi for IoT).
/// It requires a dedicated Huawei NearLink hardware SDK and is only available on
/// HarmonyOS/OpenHarmony devices with NearLink silicon.
///
/// <b>HARDWARE BLOCKED:</b> <see cref="IsAvailable"/> returns <see langword="false"/>.
/// All data-path methods throw <see cref="NotSupportedException"/>.
///
/// When Huawei publishes a Windows NearLink SDK, replace this stub with a real implementation.
/// </summary>
public sealed class WinNearLinkStubTransportService : INearLinkTransportService
{
    /// <inheritdoc />
    public string Name => "Aether Teal (NearLink)";

    /// <inheritdoc />
    public bool IsAvailable => false; // Huawei NearLink SDK not available on Windows

    /// <inheritdoc />
    public long MaxBandwidthBps => 12_000_000; // 12 Mbps per NearLink spec

    /// <inheritdoc />
    public int MaxRangeMeters => 600; // Up to 600 m per NearLink spec

    /// <inheritdoc />
    public int PowerCostRelative => 1; // 60% less power than BLE

    /// <inheritdoc />
    public int MaxConcurrentPeers => 500; // 500+ concurrent peers per NearLink spec

    /// <inheritdoc />
    public int ConnectedPeerCount => 0;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event Action<string>? PeerConnected
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "NearLink (Aether Teal) requires a Huawei hardware SDK. " +
            "IsAvailable = false on Windows.");

    /// <inheritdoc />
    public Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "NearLink (Aether Teal) requires a Huawei hardware SDK. " +
            "IsAvailable = false on Windows.");

    /// <inheritdoc />
    public bool IsConnected(string peerUhid) => false;
}
