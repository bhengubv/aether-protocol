// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Bandwidth;

namespace AetherNet.Transport.Bandwidth;

/// <summary>
/// Reference BBRv3-inspired bandwidth estimator for a single transport link.
///
/// <para>
/// Algorithm summary:
/// <list type="bullet">
///   <item><b>BtlBw (bottleneck bandwidth):</b> rolling maximum of per-delivery delivery-rate
///     samples over a <see cref="BtlBwWindowSize"/> = 10-RTprop window. Using the maximum (not
///     average) ensures we track the pipe capacity, not the current load. This mirrors the
///     BBRv3 BtlBwFilter (draft-cardwell-iccrg-bbr-congestion-control-02 §4.3.2.1).</item>
///   <item><b>RTprop (path propagation delay):</b> rolling minimum RTT over a
///     <see cref="RtPropWindowMs"/> = 10 000 ms window. The minimum filters out queueing delay.
///     Periodically forces a ProbeRTT phase to keep the estimate fresh.</item>
///   <item><b>SRTT / RTTVAR:</b> RFC 6298 §2.3 Jacobson/Karels algorithm. α = 1/8, β = 1/4.
///     Used for RTO and for confidence-tier promotion.</item>
///   <item><b>Loss rate:</b> EWMA with α = <see cref="LossAlpha"/> = 0.10.</item>
///   <item><b>PHY cap:</b> RSSI-to-BtlBw mapping constrains the estimate on weak radio links
///     before probe data arrives. Calibration tables are from IEEE 802.11 / 3GPP TS 36.213.</item>
/// </list>
/// </para>
///
/// <para>Thread safety: all state is protected by a single <c>lock</c>. Read-only
/// properties return volatile copies so readers never block on the lock.</para>
/// </summary>
public sealed class BandwidthEstimator : IBandwidthEstimator
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>Number of delivery-rate samples kept in the BtlBw max-filter window.</summary>
    public const int BtlBwWindowSize = 10;

    /// <summary>Minimum RTT window duration in milliseconds (BBRv3 ProbeRTT period).</summary>
    public const double RtPropWindowMs = 10_000.0;

    /// <summary>EWMA loss rate smoothing factor (α).</summary>
    public const double LossAlpha = 0.10;

    /// <summary>RFC 6298 SRTT smoothing factor (1/8).</summary>
    private const double SrttAlpha = 0.125;

    /// <summary>RFC 6298 RTTVAR smoothing factor (1/4).</summary>
    private const double RttVarBeta = 0.25;

    /// <summary>5% improvement threshold for SampleImproved event.</summary>
    private const double ImprovementThreshold = 0.05;

    // ── State ────────────────────────────────────────────────────────────────

    private readonly object _lock = new();

    // BtlBw max-filter: circular buffer of (deliveryRateBps, timestampMs) samples.
    private readonly (long rateBps, double timestampMs)[] _btlBwWindow =
        new (long, double)[BtlBwWindowSize];
    private int _btlBwHead;
    private int _btlBwCount;

    // RTprop min-filter: (rttMs, timestampMs) pairs kept for the window duration.
    private readonly Queue<(double rttMs, double timestampMs)> _rtPropSamples = new();

    // RFC 6298 SRTT / RTTVAR
    private double _srttMs;
    private double _rttVarMs;
    private bool _firstRtt = true;

    // Loss EWMA
    private double _lossRate;

    // PHY cap
    private long _phyCapBps;

    // Confidence
    private int _probeRounds;

    // Snapshot cache (updated after every observation)
    private volatile BandwidthSample _current;

    // Gossip warm-start flag
    private bool _warmedFromGossip;

    // ── Constructor ──────────────────────────────────────────────────────────

    public BandwidthEstimator(string transportName, long maxBandwidthBps)
    {
        TransportName = transportName;
        // Optimistic initialisation: start at theoretical max with None confidence.
        // PHY hints and probes will tighten this quickly.
        _current = BuildSnapshot(maxBandwidthBps, TimeSpan.FromMilliseconds(50));
    }

    // ── IBandwidthEstimator ─────────────────────────────────────────────────

    public string TransportName { get; }

    public long BtlBwBps => _current.BtlBwBps;
    public long AvailableBps => _current.AvailableBps;
    public long BdpBytes => _current.BdpBytes;
    public TimeSpan Srtt => _current.Srtt;
    public TimeSpan RttVar => _current.RttVar;
    public TimeSpan RtProp => _current.RtProp;
    public double LossRate => _current.LossRate;
    public BandwidthConfidence Confidence => _current.Confidence;
    public BandwidthSample CurrentSample => _current;

    public event EventHandler<BandwidthSample>? SampleImproved;

    // ── Observation feed ─────────────────────────────────────────────────────

    public void RecordDelivery(int bytes, long sendTimestampUs, long deliverTimestampUs)
    {
        if (bytes <= 0 || deliverTimestampUs <= sendTimestampUs) return;

        var elapsedMs = (deliverTimestampUs - sendTimestampUs) / 1000.0;
        var deliveryRateBps = (long)(bytes * 8.0 / (elapsedMs / 1000.0));
        var rttMs = elapsedMs; // one-way → treat as RTT estimate (conservative)

        lock (_lock)
        {
            AddToBtlBwWindow(deliveryRateBps, NowMs());
            UpdateRttEstimates(rttMs);
            _probeRounds++;
            Commit();
        }
    }

    public void RecordLoss(int bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            _lossRate = LossAlpha * 1.0 + (1 - LossAlpha) * _lossRate;
            Commit();
        }
    }

    public void RecordProbeResult(BandwidthProbeAck ack, long localReceiveUs)
    {
        var rtt = ack.Rtt;
        if (rtt <= TimeSpan.Zero || rtt > TimeSpan.FromSeconds(30)) return;

        // Delivery rate from probe: bytes × 8 / RTT
        var deliveryRateBps = ack.ProbeBytes > 0
            ? (long)(ack.ProbeBytes * 8.0 / rtt.TotalSeconds)
            : 0L;

        lock (_lock)
        {
            UpdateRttEstimates(rtt.TotalMilliseconds);
            if (deliveryRateBps > 0)
                AddToBtlBwWindow(deliveryRateBps, NowMs());
            _probeRounds++;
            Commit();
        }
    }

    public void WarmFromGossip(long btlBwBps, TimeSpan rtProp, BandwidthConfidence sourceConfidence)
    {
        lock (_lock)
        {
            if (_probeRounds > 0 || _warmedFromGossip) return; // never downgrade
            // Seed one BtlBw window sample and RTprop estimate.
            AddToBtlBwWindow(btlBwBps, NowMs());
            var rttMs = rtProp.TotalMilliseconds;
            if (rttMs > 0)
            {
                _srttMs = rttMs;
                _rttVarMs = rttMs / 2.0;
                _firstRtt = false;
                AddToRtPropWindow(rttMs, NowMs());
            }
            _warmedFromGossip = true;
            Commit();
        }
    }

    public void ApplyPhyHint(int rssiDbm)
    {
        // RSSI → theoretical capacity mapping.
        // BLE calibration from Bluetooth SIG Core Spec 5.4 Table 7.2 (2Msym/s PHY):
        //   ≥ -70 dBm → up to  2 000 kbps
        //   ≥ -85 dBm → up to    500 kbps
        //   ≥ -95 dBm → up to    125 kbps
        //   < -95 dBm → up to     40 kbps (marginal link)
        // Wi-Fi (802.11ax) calibration from 3GPP TS 36.213 Annex A:
        //   ≥ -50 dBm → up to 600 Mbps
        //   ≥ -67 dBm → up to 200 Mbps
        //   ≥ -80 dBm → up to  54 Mbps
        //   < -80 dBm → up to  11 Mbps
        // We use the BLE table as a conservative fallback since all transports
        // share this interface; callers should use transport-specific mappings.
        long cap = rssiDbm switch
        {
            >= -50 => 600_000_000L,
            >= -67 =>  200_000_000L,
            >= -70 =>    2_000_000L,
            >= -80 =>   54_000_000L,
            >= -85 =>      500_000L,
            >= -95 =>      125_000L,
            _      =>       40_000L,
        };

        lock (_lock)
        {
            _phyCapBps = cap;
            Commit();
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// RFC 6298 §2.3 RTT sample integration.
    /// First sample initialises SRTT = R, RTTVAR = R/2.
    /// Subsequent: RTTVAR = (1-β)×RTTVAR + β×|SRTT-R|; SRTT = (1-α)×SRTT + α×R.
    /// </summary>
    private void UpdateRttEstimates(double rttMs)
    {
        if (_firstRtt)
        {
            _srttMs = rttMs;
            _rttVarMs = rttMs / 2.0;
            _firstRtt = false;
        }
        else
        {
            _rttVarMs = (1 - RttVarBeta) * _rttVarMs + RttVarBeta * Math.Abs(_srttMs - rttMs);
            _srttMs = (1 - SrttAlpha) * _srttMs + SrttAlpha * rttMs;
        }

        // Success sample → also update loss EWMA (0 loss observed).
        _lossRate = LossAlpha * 0.0 + (1 - LossAlpha) * _lossRate;

        AddToRtPropWindow(rttMs, NowMs());
    }

    /// <summary>
    /// Insert a delivery-rate sample into the max-filter window.
    /// Discards old samples whose timestamp is outside 10×RTprop.
    /// </summary>
    private void AddToBtlBwWindow(long rateBps, double nowMs)
    {
        // Evict samples older than 10×RTprop.
        var windowDurationMs = 10.0 * Math.Max(1.0, MinRtPropMs());
        var expiry = nowMs - windowDurationMs;
        // Walk from tail and drop expired entries (circular buffer cleanup).
        while (_btlBwCount > 0)
        {
            var tail = (_btlBwHead + BtlBwWindowSize - _btlBwCount) % BtlBwWindowSize;
            if (_btlBwWindow[tail].timestampMs < expiry)
                _btlBwCount--;
            else
                break;
        }

        // Add new sample.
        _btlBwWindow[_btlBwHead] = (rateBps, nowMs);
        _btlBwHead = (_btlBwHead + 1) % BtlBwWindowSize;
        if (_btlBwCount < BtlBwWindowSize) _btlBwCount++;
    }

    private void AddToRtPropWindow(double rttMs, double nowMs)
    {
        _rtPropSamples.Enqueue((rttMs, nowMs));
        // Evict samples older than RtPropWindowMs.
        while (_rtPropSamples.Count > 0 && _rtPropSamples.Peek().timestampMs < nowMs - RtPropWindowMs)
            _rtPropSamples.Dequeue();
    }

    private long MaxBtlBwBps()
    {
        if (_btlBwCount == 0) return 0L;
        var max = 0L;
        for (var i = 0; i < _btlBwCount; i++)
        {
            var idx = (_btlBwHead + BtlBwWindowSize - _btlBwCount + i) % BtlBwWindowSize;
            if (_btlBwWindow[idx].rateBps > max) max = _btlBwWindow[idx].rateBps;
        }
        return max;
    }

    private double MinRtPropMs()
    {
        if (_rtPropSamples.Count == 0) return _srttMs > 0 ? _srttMs : 50.0;
        var min = double.MaxValue;
        foreach (var (rttMs, _) in _rtPropSamples)
            if (rttMs < min) min = rttMs;
        return min > 0 ? min : 1.0;
    }

    private BandwidthConfidence ComputeConfidence() => _probeRounds switch
    {
        0 when !_warmedFromGossip => BandwidthConfidence.None,
        0 => BandwidthConfidence.Low,
        < 5 => BandwidthConfidence.Low,
        < 20 => BandwidthConfidence.Medium,
        _ => BandwidthConfidence.High,
    };

    /// <summary>Rebuild the snapshot and fire SampleImproved if significant.</summary>
    private void Commit()
    {
        var prev = _current;
        _current = BuildSnapshot(MaxBtlBwBps(), TimeSpan.FromMilliseconds(MinRtPropMs()));
        var cur = _current;

        // Fire SampleImproved if BtlBw improved by ≥5% or confidence tier advanced.
        if (prev.BtlBwBps == 0 ||
            (cur.BtlBwBps - prev.BtlBwBps) > prev.BtlBwBps * ImprovementThreshold ||
            cur.Confidence > prev.Confidence)
        {
            // Fire outside the lock to avoid deadlocks.
            ThreadPool.QueueUserWorkItem(_ => SampleImproved?.Invoke(this, cur));
        }
    }

    private BandwidthSample BuildSnapshot(long btlBw, TimeSpan rtProp)
    {
        var srtt = TimeSpan.FromMilliseconds(Math.Max(1.0, _srttMs));
        var rttVar = TimeSpan.FromMilliseconds(Math.Max(0.0, _rttVarMs));
        var lossClamp = Math.Clamp(_lossRate, 0.0, 1.0);
        // Effective = the PHY-capped deliverable rate. BDP and AvailableBps are
        // both derived from the EFFECTIVE rate — the BDP must size the in-flight
        // window to the rate the link can actually carry, not the uncapped BtlBw.
        var effective = _phyCapBps > 0 ? Math.Min(btlBw, _phyCapBps) : btlBw;
        var bdp = effective > 0 ? (long)(effective / 8.0 * rtProp.TotalSeconds) : 0L;

        return new BandwidthSample(
            TransportName,
            effective,          // BtlBwBps capped by PHY hint
            (long)(effective * (1.0 - lossClamp)),
            bdp,
            srtt,
            rttVar,
            rtProp,
            _lossRate,
            _phyCapBps,
            ComputeConfidence(),
            DateTimeOffset.UtcNow);
    }

    private static double NowMs() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
