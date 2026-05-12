// SPDX-License-Identifier: MIT

namespace Aether.Reputation;

/// <summary>
/// Tuneable thresholds for <see cref="BehavioralAnomalyDetector"/>.
/// All properties have production-ready defaults; override in unit tests to
/// trigger anomalies with small synthetic packet counts.
/// </summary>
public sealed class AnomalyDetectorOptions
{
    /// <summary>
    /// Duration of the sliding volume-measurement window in milliseconds.
    /// Default: 30 000 ms (30 seconds).
    /// </summary>
    public int VolumeWindowMs { get; set; } = 30_000;

    /// <summary>
    /// A node's packet count must exceed this multiplier × EWMA baseline to
    /// be classified as a volume spike.
    /// Default: 5.0×.
    /// </summary>
    public double VolumeSpikeMultiplier { get; set; } = 5.0;

    /// <summary>
    /// EWMA smoothing factor for the per-node packet-rate baseline.
    /// Lower α ⟹ slower adaptation; higher α ⟹ faster.
    /// Default: 0.20.
    /// </summary>
    public double EwmaAlpha { get; set; } = 0.20;

    /// <summary>
    /// Duration of the sliding destination-scatter window in milliseconds.
    /// Default: 60 000 ms (60 seconds).
    /// </summary>
    public int ScatterWindowMs { get; set; } = 60_000;

    /// <summary>
    /// Maximum number of unique destination UHIDs a source may contact within
    /// <see cref="ScatterWindowMs"/> before being flagged.
    /// Default: 50 destinations.
    /// </summary>
    public int ScatterThreshold { get; set; } = 50;

    /// <summary>
    /// Number of geohash characters to compare when detecting location spoofing.
    /// 4 chars ≈ 50 km × 50 km cell; 6 chars ≈ 1.2 km × 0.6 km.
    /// Default: 4.
    /// </summary>
    public int GeohashPrefixLength { get; set; } = 4;

    /// <summary>
    /// Minimum time in milliseconds between consecutive geohash-mismatch signals
    /// for the same node. Prevents a single mobile node from being hammered by a
    /// flood of spoofing penalties while it is genuinely in motion.
    /// Default: 60 000 ms (60 seconds).
    /// </summary>
    public int GeohashRateLimitMs { get; set; } = 60_000;
}
