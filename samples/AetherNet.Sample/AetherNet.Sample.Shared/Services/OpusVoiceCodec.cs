// SPDX-License-Identifier: MIT

using AetherNet.Voice;
using Concentus;
using Concentus.Enums;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Opus, which is what makes voice possible on a mesh at all.
///
/// <para>
/// Raw 16 kHz mono PCM is 256 kbps. Measured BLE between these two handsets carries about 5 kbps, so
/// uncompressed voice is not slightly too big for the radio — it is fifty times too big. Opus at
/// 24 kbps is intelligible wideband speech, which fits Wi-Fi Direct with room to spare and is the
/// right order of magnitude to keep chasing on the slower radios.
/// </para>
///
/// <para>
/// A codec instance is stateful and belongs to exactly one call: the encoder carries the prediction
/// state that makes each frame small, and handing the same instance to two calls would have them
/// decode each other's history as their own.
/// </para>
/// </summary>
public sealed class OpusVoiceCodec : IVoiceCodec, IDisposable
{
    /// <summary>
    /// Wideband. Speech is intelligible at 8 kHz and sounds like a person at 16 kHz, and the step up
    /// to 48 kHz costs bitrate to carry frequencies a human voice does not put there.
    /// </summary>
    public const int DefaultSampleRateHz = 16_000;

    /// <summary>
    /// 20 ms — the usual voice frame. Shorter frames mean more packets and more per-packet overhead
    /// on a radio that charges dearly for each one; longer frames mean the whole frame is lost when
    /// one is dropped, and add their own duration to the delay before anyone hears anything.
    /// </summary>
    public const int DefaultFrameDurationMs = 20;

    /// <summary>
    /// Enough for wideband speech, cheap enough to be worth attempting on a constrained link. Opus
    /// treats this as a target and spends less on silence.
    /// </summary>
    public const int DefaultBitrateBps = 24_000;

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private bool _disposed;

    public string Name => "opus";
    public int SampleRateHz { get; }
    public int FrameDurationMs { get; }

    /// <summary>Samples in one frame, which both ends must agree on for the audio to line up.</summary>
    public int FrameSamples => SampleRateHz * FrameDurationMs / 1000;

    public OpusVoiceCodec(
        int sampleRateHz = DefaultSampleRateHz,
        int frameDurationMs = DefaultFrameDurationMs,
        int bitrateBps = DefaultBitrateBps)
    {
        if (!IsSupportedSampleRate(sampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz),
                sampleRateHz, "Opus supports 8000, 12000, 16000, 24000 or 48000 Hz.");
        if (!IsSupportedFrameDuration(frameDurationMs))
            throw new ArgumentOutOfRangeException(nameof(frameDurationMs),
                frameDurationMs, "Opus frames are 2.5, 5, 10, 20, 40 or 60 ms; whole milliseconds only here.");

        SampleRateHz = sampleRateHz;
        FrameDurationMs = frameDurationMs;

        // VoIP mode rather than Audio: it optimises for intelligible speech over musical fidelity,
        // which is the whole job here.
        _encoder = OpusCodecFactory.CreateEncoder(sampleRateHz, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrateBps;

        _decoder = OpusCodecFactory.CreateDecoder(sampleRateHz, 1);
    }

    /// <summary>The sample rates Opus itself accepts — anything else is silently wrong, so it is refused.</summary>
    public static bool IsSupportedSampleRate(int hz) =>
        hz is 8_000 or 12_000 or 16_000 or 24_000 or 48_000;

    public static bool IsSupportedFrameDuration(int ms) =>
        ms is 5 or 10 or 20 or 40 or 60;

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm.Length != FrameSamples)
            throw new ArgumentException(
                $"Opus encodes whole frames: expected {FrameSamples} samples, got {pcm.Length}.", nameof(pcm));

        // Comfortably larger than any frame Opus will produce at these settings; the encoder returns
        // the length it actually used and we keep only that.
        var scratch = new byte[FrameSamples * 2];
        var written = _encoder.Encode(pcm, FrameSamples, scratch, scratch.Length);

        var frame = new byte[written];
        scratch.AsSpan(0, written).CopyTo(frame);
        return frame;
    }

    /// <summary>
    /// Decode a frame — or, given nothing, conceal a lost one.
    ///
    /// <para>
    /// An empty span means the frame never arrived. Opus is told so explicitly, and answers with its
    /// own guess at what was in the gap, which sounds far better than the click of a hole in the
    /// audio. On a mesh, frames <b>will</b> go missing.
    /// </para>
    /// </summary>
    public short[] Decode(ReadOnlySpan<byte> encoded)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pcm = new short[FrameSamples];
        if (encoded.IsEmpty)
        {
            _decoder.Decode(null, pcm, FrameSamples);
            return pcm;
        }

        _decoder.Decode(encoded, pcm, FrameSamples);
        return pcm;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_encoder as IDisposable)?.Dispose();
        (_decoder as IDisposable)?.Dispose();
    }
}
