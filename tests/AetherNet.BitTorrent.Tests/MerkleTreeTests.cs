// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.V2;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class MerkleTreeTests
{
    private const int Block = MerkleTree.BlockSize;

    private static byte[] Filled(int len)
    {
        var a = new byte[len];
        for (int i = 0; i < len; i++) a[i] = (byte)(i * 7 + 1);
        return a;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    [Fact]
    public void Single_block_root_is_its_sha256()
    {
        var data = Filled(100);
        Assert.Equal(SHA256.HashData(data), MerkleTree.ComputeRoot(data));
    }

    [Fact]
    public void Two_blocks_root_is_sha256_of_concatenated_leaf_hashes()
    {
        var data = Filled(Block + 50);
        var h0 = SHA256.HashData(data[..Block]);
        var h1 = SHA256.HashData(data[Block..]);
        Assert.Equal(SHA256.HashData(Concat(h0, h1)), MerkleTree.ComputeRoot(data));
    }

    [Fact]
    public void Three_blocks_pad_to_four_with_a_zero_hash()
    {
        var data = Filled(2 * Block + 10);
        var h0 = SHA256.HashData(data[..Block]);
        var h1 = SHA256.HashData(data[Block..(2 * Block)]);
        var h2 = SHA256.HashData(data[(2 * Block)..]);
        var zero = new byte[32];

        var left = SHA256.HashData(Concat(h0, h1));
        var right = SHA256.HashData(Concat(h2, zero));
        var expected = SHA256.HashData(Concat(left, right));

        Assert.Equal(expected, MerkleTree.ComputeRoot(data));
    }

    [Fact]
    public void Empty_content_has_a_zero_root() =>
        Assert.Equal(new byte[32], MerkleTree.ComputeRoot(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void V2_info_hash_is_sha256_of_the_info_dict()
    {
        var info = new BencodeDictionary();
        info.Add("meta version", new BencodeInteger(2));
        info.Add("name", new BencodeString("v2.bin"));
        info.Add("piece length", new BencodeInteger(65536));
        var bytes = info.Encode();

        Assert.Equal(SHA256.HashData(bytes), BitTorrentV2.InfoHash(bytes));
        Assert.Equal(32, BitTorrentV2.InfoHash(bytes).Length);
        Assert.Equal(20, BitTorrentV2.InfoHashTruncated(bytes).Length);
        Assert.Equal(SHA256.HashData(bytes)[..20], BitTorrentV2.InfoHashTruncated(bytes));
    }
}
