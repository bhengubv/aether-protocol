// SPDX-License-Identifier: MIT

namespace Aether.Streaming;

/// <summary>
/// Latency profile for a stream session. Determines the bitrate ladder and ABR cadence.
/// </summary>
public enum StreamProfile : byte
{
    /// <summary>Real-time call: 100–150ms parents, per-child ABR.</summary>
    ProfileA = 0,
    /// <summary>Live broadcast: 500ms–1s parents, per-parent ABR.</summary>
    ProfileB = 1,
    /// <summary>VOD: 5–10s parents, per-parent ABR with full probe.</summary>
    ProfileC = 2,
}

/// <summary>
/// One rung on the adaptive bitrate ladder.
/// </summary>
/// <param name="Label">Short identifier ("B-mid", "C-high", …).</param>
/// <param name="AudioKbps">Audio bitrate for this rung (Kbps).</param>
/// <param name="VideoKbps">Video bitrate for this rung (Kbps).</param>
/// <param name="VideoQuality">Human-readable resolution label ("720p", "1080p", …).</param>
public record BitrateRung(string Label, int AudioKbps, int VideoKbps, string VideoQuality);

/// <summary>
/// Default bitrate ladders per profile as defined in the adaptive-secure-streaming spec.
/// Hosts may supply a custom ladder; this class provides the protocol-level defaults.
/// </summary>
public static class BitrateLadder
{
    /// <summary>Profile A (real-time call) — 3 rungs.</summary>
    public static readonly IReadOnlyList<BitrateRung> ProfileA = new[]
    {
        new BitrateRung("A-low",  16,   200, "144p"),
        new BitrateRung("A-mid",  32,   400, "240p"),
        new BitrateRung("A-high", 64,   800, "360p"),
    };

    /// <summary>Profile B (live broadcast) — 4 rungs.</summary>
    public static readonly IReadOnlyList<BitrateRung> ProfileB = new[]
    {
        new BitrateRung("B-low",   64,   800, "360p"),
        new BitrateRung("B-mid",   96,  1500, "480p"),
        new BitrateRung("B-high", 128,  3000, "720p"),
        new BitrateRung("B-max",  128,  5000, "1080p"),
    };

    /// <summary>Profile C (VOD) — 5 rungs.</summary>
    public static readonly IReadOnlyList<BitrateRung> ProfileC = new[]
    {
        new BitrateRung("C-low",    96,  1000, "480p"),
        new BitrateRung("C-mid",   128,  2500, "720p"),
        new BitrateRung("C-high",  128,  5000, "1080p"),
        new BitrateRung("C-ultra", 192,  9000, "1440p"),
        new BitrateRung("C-max",   192, 16000, "2160p"),
    };

    /// <summary>Returns the default ladder for the given profile.</summary>
    public static IReadOnlyList<BitrateRung> ForProfile(StreamProfile profile) => profile switch
    {
        StreamProfile.ProfileA => ProfileA,
        StreamProfile.ProfileB => ProfileB,
        StreamProfile.ProfileC => ProfileC,
        _ => ProfileB,
    };
}

/// <summary>
/// Selects the appropriate bitrate rung given a measured bandwidth estimate and
/// signals when the link is too degraded to sustain even the floor rung.
/// </summary>
public sealed class AdaptiveBitrateController
{
    private readonly StreamProfile _profile;
    private int _currentRungIndex;
    private long _estimatedBandwidthKbps;

    /// <param name="profile">Stream latency profile — determines which ladder to use.</param>
    /// <param name="initialBandwidthKbps">Seed bandwidth estimate used before the first real probe.</param>
    public AdaptiveBitrateController(StreamProfile profile, long initialBandwidthKbps = 10_000)
    {
        _profile = profile;
        _estimatedBandwidthKbps = initialBandwidthKbps;
        var ladder = BitrateLadder.ForProfile(profile);
        _currentRungIndex = SelectRungIndex(initialBandwidthKbps, ladder);
    }

    /// <summary>The latency profile this controller was created for.</summary>
    public StreamProfile Profile => _profile;

    /// <summary>The rung currently selected for the next segment / parent.</summary>
    public BitrateRung CurrentRung => BitrateLadder.ForProfile(_profile)[_currentRungIndex];

    /// <summary>
    /// Updates the internal bandwidth estimate and selects a new rung.
    /// </summary>
    /// <param name="measuredBandwidthKbps">Observed available bandwidth in Kbps.</param>
    /// <returns><see langword="true"/> if the selected rung changed; <see langword="false"/> if the same rung is still appropriate.</returns>
    public bool UpdateBandwidth(long measuredBandwidthKbps)
    {
        _estimatedBandwidthKbps = measuredBandwidthKbps;
        var ladder = BitrateLadder.ForProfile(_profile);
        var newIndex = SelectRungIndex(measuredBandwidthKbps, ladder);
        if (newIndex == _currentRungIndex) return false;
        _currentRungIndex = newIndex;
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the current bandwidth estimate cannot sustain
    /// even the floor rung. Callers should emit a <see cref="Aether.Protocol.PacketType.StreamAbandon"/>
    /// packet instead of a segment when this is true.
    /// </summary>
    public bool ShouldAbandon()
    {
        var ladder = BitrateLadder.ForProfile(_profile);
        var floor = ladder[0];
        return _estimatedBandwidthKbps < floor.AudioKbps + floor.VideoKbps;
    }

    // Walk from highest rung down; pick the first one the bandwidth can carry with
    // 20% headroom for protocol overhead.
    private static int SelectRungIndex(long bandwidthKbps, IReadOnlyList<BitrateRung> ladder)
    {
        for (int i = ladder.Count - 1; i >= 0; i--)
        {
            var total = ladder[i].AudioKbps + ladder[i].VideoKbps;
            if (bandwidthKbps >= (long)(total * 1.2)) return i;
        }
        return 0; // fall back to floor
    }
}
