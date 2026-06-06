// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using AetherNet.Transport.Fec;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for RlncCodec, RlncEncoder, RlncDecoder and GF(2⁸) arithmetic.
/// </summary>
public sealed class RlncCodecTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[][] MakeSource(int k, int symSize)
    {
        var src = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            src[i] = new byte[symSize];
            for (int j = 0; j < symSize; j++)
                src[i][j] = (byte)((i * symSize + j) & 0xFF);
        }
        return src;
    }

    private static ReadOnlyMemory<byte>[] SplitPackets(byte[] buf, int count)
    {
        int pktSize = buf.Length / count;
        var pkts    = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < count; i++)
        {
            var seg = new byte[pktSize];
            Array.Copy(buf, i * pktSize, seg, 0, pktSize);
            pkts[i] = seg;
        }
        return pkts;
    }

    // ── RlncEncoder ──────────────────────────────────────────────────────────

    [Fact]
    public void Encoder_SystematicFirstKPackets_MatchSource()
    {
        int k = 4, sym = 8;
        byte[][] source = MakeSource(k, sym);
        var enc = new RlncEncoder(source, systematic: true);

        for (int i = 0; i < k; i++)
        {
            var (coeff, data) = enc.NextPacket();
            Assert.Equal((byte)1, coeff[i]);
            for (int j = 0; j < k; j++)
                if (j != i) Assert.Equal((byte)0, coeff[j]);
            Assert.Equal(source[i], data);
        }
    }

    [Fact]
    public void Encoder_RepairPackets_NotAllZero()
    {
        byte[][] syms = [[ 1, 2, 3 ], [ 4, 5, 6 ], [ 7, 8, 9 ]];
        var enc = new RlncEncoder(syms, systematic: false);
        for (int i = 0; i < 20; i++)
        {
            var (coeff, _) = enc.NextPacket();
            Assert.Contains(coeff, b => b != 0);
        }
    }

    // ── RlncDecoder ──────────────────────────────────────────────────────────

    [Fact]
    public void Decoder_RoundTrip_K4()
    {
        int k = 4, sym = 8;
        byte[][] source = MakeSource(k, sym);
        var enc = new RlncEncoder(source, systematic: true);
        var dec = new RlncDecoder(k, sym);

        while (!dec.IsComplete)
        {
            var (c, d) = enc.NextPacket();
            dec.AddPacket(c, d);
        }

        byte[]? decoded = dec.TryDecode();
        Assert.NotNull(decoded);
        for (int i = 0; i < k; i++)
            Assert.Equal(source[i], decoded!.AsSpan(i * sym, sym).ToArray());
    }

    [Fact]
    public void Decoder_ExactlyKSystematic_Complete()
    {
        int k = 3, sym = 4;
        byte[][] source = MakeSource(k, sym);
        var enc = new RlncEncoder(source, systematic: true);
        var dec = new RlncDecoder(k, sym);

        for (int i = 0; i < k; i++)
        {
            var (c, d) = enc.NextPacket();
            dec.AddPacket(c, d);
        }

        Assert.True(dec.IsComplete);
        Assert.Equal(k, dec.Rank);
    }

    [Fact]
    public void Decoder_LinearlyDependentPacket_DoesNotIncreaseRank()
    {
        int k = 2, sym = 4;
        byte[][] source = MakeSource(k, sym);
        var enc = new RlncEncoder(source, systematic: true);
        var dec = new RlncDecoder(k, sym);

        var (c0, d0) = enc.NextPacket();
        dec.AddPacket(c0, d0);
        int rankBefore = dec.Rank;
        dec.AddPacket(c0, d0); // duplicate
        Assert.Equal(rankBefore, dec.Rank);
    }

    [Fact]
    public void Decoder_IsComplete_AtRankK()
    {
        int k = 2, sym = 3;
        byte[][] source = MakeSource(k, sym);
        var enc = new RlncEncoder(source, systematic: true);
        var dec = new RlncDecoder(k, sym);

        Assert.False(dec.IsComplete);
        var (c0, d0) = enc.NextPacket(); dec.AddPacket(c0, d0);
        Assert.False(dec.IsComplete);
        var (c1, d1) = enc.NextPacket(); dec.AddPacket(c1, d1);
        Assert.True(dec.IsComplete);
    }

    [Fact]
    public void Decoder_RepairOnly_RoundTrip()
    {
        int k = 4, sym = 8;
        byte[][] source = MakeSource(k, sym);
        var enc = new RlncEncoder(source, systematic: false);
        var dec = new RlncDecoder(k, sym);

        int attempts = 0;
        while (!dec.IsComplete)
        {
            var (c, d) = enc.NextPacket();
            dec.AddPacket(c, d);
            Assert.True(++attempts < 200, "repair-only decoder stalled");
        }

        byte[]? decoded = dec.TryDecode();
        Assert.NotNull(decoded);
        for (int i = 0; i < k; i++)
            Assert.Equal(source[i], decoded!.AsSpan(i * sym, sym).ToArray());
    }

    // ── RlncCodec ────────────────────────────────────────────────────────────

    [Fact]
    public void Codec_Metadata_IsCorrect()
    {
        var codec = new RlncCodec(16);
        Assert.Equal("RLNC-GF256", codec.CodecName);
        Assert.Equal(0, codec.DeviceTierRequired);
        Assert.Equal(0.05, codec.OverheadFraction, precision: 9);
        Assert.Equal(0, codec.FixedSymbolSizeBytes);
    }

    [Fact]
    public void Codec_K1_SingleSymbol_RoundTrip()
    {
        var codec  = new RlncCodec(1);
        byte[] src = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] encoded = codec.Encode(src, targetSymbolCount: 2);
        var    pkts    = SplitPackets(encoded, 2);
        Assert.True(codec.TryDecode(pkts, 1, out byte[]? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(src, decoded!.AsSpan(0, src.Length).ToArray());
    }

    [Fact]
    public void Codec_LargePayload_RoundTrip()
    {
        var codec = new RlncCodec(16);
        byte[] src = new byte[1024];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i & 0xFF);

        byte[] encoded = codec.Encode(src, targetSymbolCount: 20);
        var    pkts    = SplitPackets(encoded, 20);
        Assert.True(codec.TryDecode(pkts, 16, out byte[]? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(src, decoded!.AsSpan(0, src.Length).ToArray());
    }

    [Fact]
    public void Codec_DecodeWithLosses_Succeeds()
    {
        var codec = new RlncCodec(16);
        byte[] src = new byte[512];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i & 0xFF);

        byte[] encoded = codec.Encode(src, targetSymbolCount: 20);
        var    pkts    = SplitPackets(encoded, 20);

        // Remove 4 packets (indices 0, 3, 7, 11 in descending order to preserve indices).
        var pktList = new List<ReadOnlyMemory<byte>>(pkts);
        foreach (int idx in new[] { 11, 7, 3, 0 })
            pktList.RemoveAt(idx);

        Assert.True(codec.TryDecode([..pktList], 16, out byte[]? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(src, decoded!.AsSpan(0, src.Length).ToArray());
    }
}
