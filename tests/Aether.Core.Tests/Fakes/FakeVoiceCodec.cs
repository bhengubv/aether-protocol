// SPDX-License-Identifier: MIT

using Aether.Voice;

namespace Aether.Core.Tests.Fakes;

/// <summary>
/// Trivial non-stateful codec for unit tests. <see cref="Encode"/> packs PCM samples
/// little-endian; <see cref="Decode"/> reverses that. Equivalent to
/// <see cref="PassThroughVoiceCodec"/> but lives in test-fakes so a future change to the
/// production passthrough doesn't quietly alter test semantics.
/// </summary>
public sealed class FakeVoiceCodec : IVoiceCodec
{
    public string Name { get; }
    public int SampleRateHz { get; }
    public int FrameDurationMs { get; }

    /// <summary>How many times <see cref="Encode"/> was called.</summary>
    public int EncodeCount { get; private set; }

    /// <summary>How many times <see cref="Decode"/> was called.</summary>
    public int DecodeCount { get; private set; }

    public FakeVoiceCodec(string name = "fake", int sampleRateHz = 16_000, int frameDurationMs = 20)
    {
        Name = name;
        SampleRateHz = sampleRateHz;
        FrameDurationMs = frameDurationMs;
    }

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        EncodeCount++;
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
        DecodeCount++;
        if ((encoded.Length & 1) != 0)
            throw new ArgumentException("FakeVoiceCodec requires an even number of bytes", nameof(encoded));
        var pcm = new short[encoded.Length / 2];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(encoded[i * 2] | (encoded[i * 2 + 1] << 8));
        return pcm;
    }
}
