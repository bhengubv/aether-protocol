// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Content.Models;
using Xunit;

namespace AetherNet.Cards.Tests;

/// <summary>
/// Content-addressing = tamper-evidence. A card's blob is identified by its root hash, so any flipped
/// byte changes the address — a carrier cannot alter the bytes undetected.
/// </summary>
public class ContentIntegrityTests
{
    [Fact]
    public void FlippedChunkByte_FailsChunkVerification()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 1000));
        var descriptor = ContentDescriptor.FromBytes("blob", data, "application/octet-stream", 256);

        var chunk0 = data.AsSpan(0, 256).ToArray();
        Assert.True(descriptor.VerifyChunk(0, chunk0));

        chunk0[10] ^= 0xFF; // inject one bad byte
        Assert.False(descriptor.VerifyChunk(0, chunk0));
    }

    [Fact]
    public void TamperedRootHash_FailsSelfVerification()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var descriptor = ContentDescriptor.FromBytes("blob", data);
        Assert.True(descriptor.VerifySelf());

        descriptor.RootHash = "0000" + descriptor.RootHash[4..]; // corrupt the manifest's own root
        Assert.False(descriptor.VerifySelf());
    }
}
