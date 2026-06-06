// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.Models;

/// <summary>
/// Represents a BLE advertisement packet used for passive peer discovery.
/// Nodes broadcast advertisements containing their UHID and capabilities
/// so that nearby nodes can discover them without establishing a connection.
/// </summary>
public sealed class BleAdvertisement
{
    /// <summary>The Universal Hash ID of the advertising node.</summary>
    public string SourceUhid { get; set; } = string.Empty;

    /// <summary>Signal strength in dBm, used to estimate distance.</summary>
    public int Rssi { get; set; }

    /// <summary>Bitfield of capabilities supported by this node (e.g. Streaming, Voice, DtnCarrier).</summary>
    public int Capabilities { get; set; }

    /// <summary>Arbitrary payload data included in the advertisement.</summary>
    public byte[] Payload { get; set; } = [];

    /// <summary>UTC timestamp when this advertisement was created.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Protocol version advertised by this node.</summary>
    public byte ProtocolVersion { get; set; } = 2;
}

/// <summary>
/// Cumulative transport metrics across all active transports.
/// Used for monitoring, diagnostics, and adaptive transport selection.
/// </summary>
public sealed class TransportMetrics
{
    /// <summary>Total number of sends attempted via BLE.</summary>
    public long BleSendCount { get; set; }

    /// <summary>Total bytes sent via BLE.</summary>
    public long BleBytesSent { get; set; }

    /// <summary>Total number of sends attempted via Wi-Fi Direct.</summary>
    public long WifiDirectSendCount { get; set; }

    /// <summary>Total bytes sent via Wi-Fi Direct.</summary>
    public long WifiDirectBytesSent { get; set; }

    /// <summary>Total number of sends attempted via NearLink.</summary>
    public long NearLinkSendCount { get; set; }

    /// <summary>Total bytes sent via NearLink.</summary>
    public long NearLinkBytesSent { get; set; }

    /// <summary>Total number of sends attempted via CircleLink or other custom transports.</summary>
    public long CircleLinkSendCount { get; set; }

    /// <summary>Total bytes sent via CircleLink or other custom transports.</summary>
    public long CircleLinkBytesSent { get; set; }

    /// <summary>Total number of sends attempted via additional registered transports.</summary>
    public long AdditionalSendCount { get; set; }

    /// <summary>Total bytes sent via additional registered transports.</summary>
    public long AdditionalBytesSent { get; set; }

    /// <summary>Total send failures across all transports.</summary>
    public long TotalFailures { get; set; }

    /// <summary>Total successful sends across all transports.</summary>
    public long TotalSuccesses => BleSendCount + WifiDirectSendCount + NearLinkSendCount
                                  + CircleLinkSendCount + AdditionalSendCount - TotalFailures;

    /// <summary>Total bytes sent across all transports.</summary>
    public long TotalBytesSent => BleBytesSent + WifiDirectBytesSent + NearLinkBytesSent
                                  + CircleLinkBytesSent + AdditionalBytesSent;
}

/// <summary>
/// Per-transport EWMA metrics used by <c>PredictiveTransportSelector</c>.
/// Thread-safe via <c>lock</c>.
/// </summary>
public sealed class PerTransportMetrics
{
    private const double Alpha = 0.10; // EWMA smoothing factor

    private readonly object _lock = new();

    private double _ewmaRttMs;
    private double _ewmaLossRate;
    private double _ewmaThroughputBps;
    private bool _hasData;

    /// <summary>EWMA loss rate in [0, 1]. 0 = no loss, 1 = all failed.</summary>
    public double EwmaLossRate { get { lock (_lock) return _ewmaLossRate; } }

    /// <summary>EWMA throughput in bits per second.</summary>
    public double EwmaThroughputBps { get { lock (_lock) return _ewmaThroughputBps; } }

    /// <summary>EWMA round-trip time in milliseconds.</summary>
    public double EwmaRttMs { get { lock (_lock) return _ewmaRttMs; } }

    /// <summary>
    /// Record a single link observation. <paramref name="rttMs"/> is ignored when
    /// <paramref name="success"/> is false (failure doesn't provide a valid RTT).
    /// </summary>
    public void RecordSample(long rttMs, bool success, long bytesTransferred)
    {
        lock (_lock)
        {
            var lossObservation = success ? 0.0 : 1.0;
            if (!_hasData)
            {
                _ewmaLossRate = lossObservation;
                _ewmaRttMs = success && rttMs > 0 ? rttMs : 0.0;
                _ewmaThroughputBps = success && rttMs > 0 && bytesTransferred > 0
                    ? bytesTransferred * 8.0 / (rttMs / 1000.0)
                    : 0.0;
                _hasData = true;
            }
            else
            {
                _ewmaLossRate = Alpha * lossObservation + (1 - Alpha) * _ewmaLossRate;

                if (success && rttMs > 0)
                {
                    _ewmaRttMs = Alpha * rttMs + (1 - Alpha) * _ewmaRttMs;
                    if (bytesTransferred > 0)
                    {
                        var observedBps = bytesTransferred * 8.0 / (rttMs / 1000.0);
                        _ewmaThroughputBps = Alpha * observedBps + (1 - Alpha) * _ewmaThroughputBps;
                    }
                }
            }
        }
    }
}
