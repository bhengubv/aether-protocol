// SPDX-License-Identifier: MIT

namespace Aether.Voice;

/// <summary>
/// Codec abstraction. The open-source repo ships no real audio codec — hosts wire
/// up Opus / Speex / a platform-native codec via this interface. The default
/// <see cref="PassThroughVoiceCodec"/> is identity-encoded raw PCM and exists only
/// for tests.
///
/// Implementations are free to be stateful: a codec instance lives for the
/// duration of one call session.
/// </summary>
public interface IVoiceCodec
{
    /// <summary>Codec identifier matched against <see cref="Aether.Voice.Models.VoiceSignalingMessage.ProposedCodecs"/> ("opus", "speex", "pcm-passthrough", …).</summary>
    string Name { get; }

    /// <summary>Sample rate in Hz this codec instance is configured for.</summary>
    int SampleRateHz { get; }

    /// <summary>Frame duration in ms this codec instance is configured for.</summary>
    int FrameDurationMs { get; }

    /// <summary>Encode one PCM frame to its codec representation.</summary>
    /// <param name="pcm">Linear-PCM samples, 16-bit mono, length = sample-rate × frame-duration / 1000.</param>
    byte[] Encode(ReadOnlySpan<short> pcm);

    /// <summary>Decode one codec frame back to PCM. Output length is the frame size implied by <see cref="SampleRateHz"/> and <see cref="FrameDurationMs"/>.</summary>
    short[] Decode(ReadOnlySpan<byte> encoded);
}

/// <summary>
/// Identity codec — encodes 16-bit PCM as little-endian bytes and back. Useful only for tests.
/// Not space-efficient and not what you want on the air.
/// </summary>
public sealed class PassThroughVoiceCodec : IVoiceCodec
{
    public string Name => "pcm-passthrough";
    public int SampleRateHz { get; }
    public int FrameDurationMs { get; }

    public PassThroughVoiceCodec(int sampleRateHz = 16_000, int frameDurationMs = Aether.Constants.ProtocolConstants.VoiceFrameDurationMs)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (frameDurationMs <= 0) throw new ArgumentOutOfRangeException(nameof(frameDurationMs));
        SampleRateHz = sampleRateHz;
        FrameDurationMs = frameDurationMs;
    }

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            bytes[i * 2] = (byte)(pcm[i] & 0xff);
            bytes[i * 2 + 1] = (byte)((pcm[i] >> 8) & 0xff);
        }
        return bytes;
    }

    public short[] Decode(ReadOnlySpan<byte> encoded)
    {
        if ((encoded.Length & 1) != 0)
            throw new ArgumentException("PCM passthrough requires an even number of bytes", nameof(encoded));
        var pcm = new short[encoded.Length / 2];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(encoded[i * 2] | (encoded[i * 2 + 1] << 8));
        return pcm;
    }
}
