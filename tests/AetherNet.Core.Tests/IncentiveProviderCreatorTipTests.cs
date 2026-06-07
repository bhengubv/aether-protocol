// SPDX-License-Identifier: MIT

using AetherNet.Extensibility;
using AetherNet.Protocol;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for the v1.2.0 addition <see cref="IAetherNetIncentiveProvider.RecordCreatorTipAsync"/> (Issue #61).
/// </summary>
public class IncentiveProviderCreatorTipTests
{
    [Fact]
    public async Task RecordCreatorTipAsync_DefaultImpl_IsNoOpAndReturnsCompleted()
    {
        IAetherNetIncentiveProvider provider = new DefaultProvider();

        // No throw, returns immediately.
        await provider.RecordCreatorTipAsync("creator-uhid", 5.00m, "deadbeef");
    }

    [Fact]
    public async Task RecordCreatorTipAsync_CustomImpl_ReceivesArgumentsVerbatim()
    {
        var capturer = new CapturingProvider();
        IAetherNetIncentiveProvider provider = capturer;

        await provider.RecordCreatorTipAsync("creator-zulu", 12.50m, "rootHash-abc");

        Assert.Single(capturer.Tips);
        var (creator, amount, hash) = capturer.Tips[0];
        Assert.Equal("creator-zulu", creator);
        Assert.Equal(12.50m, amount);
        Assert.Equal("rootHash-abc", hash);
    }

    [Fact]
    public async Task RecordCreatorTipAsync_AndRelayCredit_AreIndependentRecordingPaths()
    {
        var capturer = new CapturingProvider();
        IAetherNetIncentiveProvider provider = capturer;

        await provider.RecordCreatorTipAsync("author", 1.00m, "h1");
        await provider.RecordRelayAsync("node-uhid", new MeshPacket { Type = PacketType.Data });

        // Both recorded separately; the relay path doesn't pollute the tip stream and vice versa.
        Assert.Single(capturer.Tips);
        Assert.Single(capturer.Relays);
    }

    private sealed class DefaultProvider : IAetherNetIncentiveProvider
    {
        // Uses every default-method on the interface.
    }

    private sealed class CapturingProvider : IAetherNetIncentiveProvider
    {
        public List<(string Creator, decimal Amount, string ContentHash)> Tips { get; } = new();
        public List<(string Node, MeshPacket Packet)> Relays { get; } = new();

        public Task RecordCreatorTipAsync(string creatorUhid, decimal amount, string contentHash, CancellationToken cancellationToken = default)
        {
            Tips.Add((creatorUhid, amount, contentHash));
            return Task.CompletedTask;
        }

        public Task RecordRelayAsync(string relayNodeUhid, MeshPacket packet, CancellationToken cancellationToken = default)
        {
            Relays.Add((relayNodeUhid, packet));
            return Task.CompletedTask;
        }
    }
}
