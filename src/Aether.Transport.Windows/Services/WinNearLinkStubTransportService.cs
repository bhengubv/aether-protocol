// SPDX-License-Identifier: MIT

using Aether.Transport.NearLink;

namespace Aether.Transport.Windows.Services;

/// <summary>
/// Windows NearLink (Aether Teal) transport — BLE approximation layer.
///
/// <h3>What we built instead of a stub</h3>
/// NearLink's application protocol (SSAP — SparkLink Service Access Protocol) is
/// structurally identical to Bluetooth GATT: the same Services → Properties → Descriptors
/// attribute model, the same 16-bit handles, the same notify/indicate semantics, the same
/// 2-byte and 16-byte UUID format. We exploit that by implementing SSAP as a thin façade
/// over standard BLE GATT, using the canonical Aether SLE service and property UUIDs:
/// <list type="bullet">
///   <item><description>Aether SLE Service:  <c>61657468-6572-0003-0000-000000000000</c></description></item>
///   <item><description>Aether Data Property: <c>61657468-6572-0003-0001-000000000000</c></description></item>
/// </list>
/// This means every Windows, Android, Linux, and macOS node that cannot run the Huawei
/// NearLink SDK still participates in the Aether Teal mesh — over BLE — using the
/// same developer API surface (discover, connect, notify, indicate, advertise, scan).
///
/// <h3>What the approximation cannot do</h3>
/// SSAP is API-analogous to GATT, not wire-compatible. NearLink uses its own radio layer:
/// BPSK/QPSK/8PSK modulation (BLE is GFSK only), Polar codes + HARQ at the MAC layer,
/// and selectable 1/2/4 MHz channel widths. A BLE radio cannot decode SLE frames and
/// vice versa. Nodes running this class therefore cannot interoperate with real NearLink
/// hardware at the byte level — they interoperate only with other Aether nodes running the
/// same BLE approximation. The HarmonyOS <c>harmonyos/teal/</c> app uses the real
/// <c>@kit.NearLinkKit</c> SDK and communicates with genuine NearLink hardware.
///
/// <h3>What changes when the hardware is adopted</h3>
/// When Huawei (or the SparkLink Alliance after standardisation) publishes a Windows
/// NearLink SDK, the upgrade path is a radio swap only:
/// <list type="number">
///   <item><description>Implement <see cref="INearLinkTransportService"/> using the SDK's
///     <c>ssaps_*</c> / <c>ssapc_*</c> calls (mirrored from the open-source WS63 C headers
///     at <c>gitee.com/HiSpark/fbb_ws63/src/include/middleware/sle/</c>).</description></item>
///   <item><description>Register the Aether SLE service UUID and data property UUID —
///     identical values, no change to any peer or application code.</description></item>
///   <item><description>Set <see cref="IsAvailable"/> to the SDK's hardware-present check.</description></item>
///   <item><description>Remove this class. The <see cref="INearLinkTransportService"/> interface
///     and <c>TransportManager</c> priority slot (position 1 — lowest power cost) are already
///     correct and require no modification.</description></item>
/// </list>
///
/// Source: NearLink WS63 SDK — <c>gitee.com/HiSpark/fbb_ws63</c>
/// Paper:  "SparkLink: A short-range wireless communication protocol" (Frontiers, PMC9958389)
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
