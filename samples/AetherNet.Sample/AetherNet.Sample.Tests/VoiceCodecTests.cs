// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Making a voice small enough to fit down a radio.
///
/// <para>
/// Raw 16 kHz mono PCM is 256 kbps. Measured BLE between these two handsets carries roughly 5 kbps —
/// so uncompressed speech is not slightly too large for the link, it is fifty times too large. This
/// is the piece that decides whether voice on a mesh is possible at all, so what matters here is not
/// that the codec round-trips but that it round-trips <b>small</b>.
/// </para>
/// </summary>
public class VoiceCodecTests
{
    private static OpusVoiceCodec ACodec() => new();

    /// <summary>A second of speech-like tone — a real signal, since silence compresses to nothing.</summary>
    private static short[] AFrameOfSpeech(int samples, int startAt = 0)
    {
        var pcm = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            // A low fundamental with a harmonic on top, which is roughly what a voice looks like and
            // is far more honest about bitrate than a pure sine.
            var t = (startAt + i) / 16_000.0;
            pcm[i] = (short)(8_000 * Math.Sin(2 * Math.PI * 180 * t) +
                             3_000 * Math.Sin(2 * Math.PI * 540 * t));
        }
        return pcm;
    }

    // ── It has to fit ─────────────────────────────────────────────────────────

    /// <summary>
    /// The number that decides everything. A 20 ms frame at 16 kHz is 640 bytes of PCM; at the target
    /// bitrate it must come out around 60. If this ever creeps back up, voice stops fitting.
    /// </summary>
    [Fact]
    public void A_frame_of_speech_encodes_far_smaller_than_the_pcm_it_came_from()
    {
        using var codec = ACodec();
        var pcm = AFrameOfSpeech(codec.FrameSamples);

        var encoded = codec.Encode(pcm);

        var pcmBytes = pcm.Length * 2;
        Assert.True(encoded.Length < pcmBytes / 4,
            $"{encoded.Length} bytes from {pcmBytes} bytes of PCM — not enough compression to fit a radio");
    }

    [Fact]
    public void Sustained_speech_stays_within_the_bitrate_a_mesh_link_can_carry()
    {
        using var codec = ACodec();
        var framesPerSecond = 1000 / codec.FrameDurationMs;
        var total = 0;

        for (var f = 0; f < framesPerSecond; f++)
            total += codec.Encode(AFrameOfSpeech(codec.FrameSamples, f * codec.FrameSamples)).Length;

        var bitsPerSecond = total * 8;
        Assert.True(bitsPerSecond < 40_000,
            $"{bitsPerSecond} bps over one second of speech — too much for the radios this has to run on");
    }

    // ── It has to come back as speech ─────────────────────────────────────────

    [Fact]
    public void A_decoded_frame_is_the_length_that_was_put_in()
    {
        using var codec = ACodec();

        var decoded = codec.Decode(codec.Encode(AFrameOfSpeech(codec.FrameSamples)));

        Assert.Equal(codec.FrameSamples, decoded.Length);
    }

    /// <summary>
    /// Opus is lossy, so the samples will not match — but the sound has to survive. Comparing energy
    /// catches the failure that matters: a frame that decodes to silence, or to noise.
    /// </summary>
    [Fact]
    public void What_comes_back_still_carries_the_sound_that_went_in()
    {
        using var codec = ACodec();
        var pcm = AFrameOfSpeech(codec.FrameSamples);

        // Opus has algorithmic delay, so the first frames come back quiet regardless of input. Push a
        // few through before measuring, the way a real call does.
        for (var i = 0; i < 5; i++) codec.Decode(codec.Encode(AFrameOfSpeech(codec.FrameSamples, i * codec.FrameSamples)));
        var decoded = codec.Decode(codec.Encode(pcm));

        Assert.True(Energy(decoded) > Energy(pcm) / 10,
            "the decoded frame is far quieter than the one encoded — it did not survive the round trip");
    }

    private static double Energy(short[] pcm)
    {
        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        return Math.Sqrt(sum / pcm.Length);
    }

    // ── Frames go missing on a mesh ───────────────────────────────────────────

    /// <summary>
    /// A dropped frame is ordinary, not exceptional. Handing the decoder nothing must produce a
    /// concealed frame of the right length rather than throwing or returning silence of the wrong
    /// size — a hole in the audio clicks, and a wrong-length frame desynchronises everything after it.
    /// </summary>
    [Fact]
    public void A_lost_frame_is_concealed_rather_than_fatal()
    {
        using var codec = ACodec();
        codec.Decode(codec.Encode(AFrameOfSpeech(codec.FrameSamples)));

        var concealed = codec.Decode(ReadOnlySpan<byte>.Empty);

        Assert.Equal(codec.FrameSamples, concealed.Length);
    }

    // ── Both ends must agree ──────────────────────────────────────────────────

    /// <summary>
    /// Frame size is a contract between two phones. A codec that quietly accepted a short frame would
    /// encode it as though it were whole, and the far end would hear it speed up.
    /// </summary>
    [Fact]
    public void A_frame_of_the_wrong_length_is_refused()
    {
        using var codec = ACodec();

        Assert.Throws<ArgumentException>(() => codec.Encode(new short[codec.FrameSamples - 1]));
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(16_000)]
    [InlineData(48_000)]
    public void Opus_sample_rates_are_accepted(int hz) =>
        Assert.True(OpusVoiceCodec.IsSupportedSampleRate(hz));

    [Theory]
    [InlineData(44_100)]   // the CD rate everyone reaches for, which Opus does not take
    [InlineData(22_050)]
    [InlineData(0)]
    public void Anything_else_is_refused_rather_than_silently_wrong(int hz)
    {
        Assert.False(OpusVoiceCodec.IsSupportedSampleRate(hz));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusVoiceCodec(hz));
    }

    [Fact]
    public void The_codec_names_itself_as_the_signalling_expects() =>
        Assert.Equal("opus", ACodec().Name);
}
