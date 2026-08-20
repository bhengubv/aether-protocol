// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What video costs, in numbers the code can act on.
///
/// <para>
/// The same arithmetic is written down in PROTOCOL_SPEC §10.10. It lives here as well so the two
/// cannot drift: the spec explains the reasoning, this is the single place the reasoning is applied,
/// and the tests check them against each other. A limit that exists only in a document is a limit
/// nobody enforces.
/// </para>
///
/// <para>
/// Everything here is <b>derived</b> from the measured radio capacity in §5.5 and the encoder
/// settings the app uses — not measured. Video has not yet been counted between two handsets. When it
/// is, whatever is measured wins and these change.
/// </para>
/// </summary>
public static class VideoBudget
{
    /// <summary>What one encoded video stream costs, matching the encoder the phone head configures.</summary>
    public const long StreamBps = 800_000;

    /// <summary>And the voice that shares the radio with it, at the codec's default.</summary>
    public const long VoiceBps = OpusVoiceCodec.DefaultBitrateBps;

    /// <summary>
    /// The share of a link a call may take, matching <see cref="OpusVoiceCodec.BitrateFor"/>.
    ///
    /// <para>
    /// A call is two directions on one radio, plus packet overhead, plus the signalling that keeps it
    /// alive. Spending a link's whole ceiling on one direction guarantees the other starves — which
    /// is exactly what was measured on BLE, where one side sent happily while the other could not get
    /// a single write in.
    /// </para>
    /// </summary>
    private const int Margin = 3;

    /// <summary>
    /// What one phone needs on its radio to be in a video call with <paramref name="participants"/>
    /// people in total, counting itself.
    ///
    /// <para>
    /// There is no server to fan out through, so every phone encodes once and sends to everyone else.
    /// Both directions therefore grow linearly with the group, and a call that is comfortable at two
    /// is four times as expensive at five.
    /// </para>
    /// </summary>
    public static long RequiredBps(int participants)
    {
        if (participants < 2) return 0;

        var others = participants - 1;
        var oneWay = (StreamBps * others) + VoiceBps;

        // One direction times the margin — NOT times two directions as well. The margin already is
        // the return direction: the audio codec gives one direction a third of the link precisely so
        // the other third can come back and the last third can be overhead. Multiplying by two on top
        // of that would count the return trip twice and refuse links that work.
        return oneWay * Margin;
    }

    /// <summary>
    /// The largest group this radio could carry, ignoring silicon.
    ///
    /// <para>
    /// Deliberately named for what it is. Bandwidth is almost never the binding constraint on a group
    /// video call — the group owner and the number of hardware decoders bind first (§10.10) — so this
    /// answer is a ceiling, never a promise.
    /// </para>
    /// </summary>
    public static int LargestGroupTheLinkCouldCarry(long linkBps)
    {
        if (linkBps <= 0) return 0;

        var n = 2;
        while (RequiredBps(n + 1) <= linkBps) n++;

        return RequiredBps(n) <= linkBps ? n : 0;
    }

    /// <summary>
    /// What to assume about decoders when the phone will not say — and only then.
    ///
    /// <para>
    /// Four, because a host that cannot answer the question is a host nothing is known about, and
    /// showing fewer people is always recoverable while configuring a decoder that fails is not: it
    /// shows nothing at all, with no error anyone can act on.
    /// </para>
    ///
    /// <para>
    /// <b>This is not a typical number, and an earlier version of this comment claimed it was.</b> It
    /// said mid-range handsets expose two to four concurrent H.264 decoders. Asked directly, the two
    /// test phones declare <b>sixteen</b> (Kirin 710) and <b>thirty-two</b> (MT6768). That figure was
    /// reasoned rather than measured, which is the same mistake BLE's throughput number made twice.
    /// PROTOCOL_SPEC §10.10 now carries the measurement.
    /// </para>
    ///
    /// <para>
    /// So this exists only as a floor for silence. Any host that can be asked MUST be asked.
    /// </para>
    /// </summary>
    public const int AssumedDecoderCap = 4;

    /// <summary>
    /// How many people can actually be in a group video call here: whichever of the radio and the
    /// silicon gives out first.
    /// </summary>
    public static int LargestGroup(long linkBps, int decoderCap = AssumedDecoderCap)
        => Math.Min(LargestGroupTheLinkCouldCarry(linkBps), Math.Max(0, decoderCap));
}
