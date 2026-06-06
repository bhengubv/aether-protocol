// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Reputation;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ReputationGossipService"/>. Uses fakes for
/// <see cref="IPacketSigningService"/> and <see cref="IMeshSender"/> — no real
/// crypto needed here; that is covered by Security-layer tests.
/// </summary>
public sealed class ReputationGossipServiceTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public string? LocalGeohash => null;
        public List<MeshPacket> Broadcasts { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        {
            Broadcasts.Add(packet);
            return Task.FromResult(1);
        }
    }

    private sealed class FakePacketSigningService : IPacketSigningService
    {
        public bool VerifyResult { get; set; } = true;

        public Task<MeshPacket> SignPacketAsync(MeshPacket packet, CancellationToken ct = default)
        {
            packet.PacketNonce = new byte[8];
            packet.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            packet.Signature = new byte[64];
            return Task.FromResult(packet);
        }

        public Task<bool> VerifyPacketAsync(MeshPacket packet, byte[] senderPublicKey, CancellationToken ct = default)
            => Task.FromResult(VerifyResult);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static MeshPacket MakeValidGossipPacket(
        string reporterUhid,
        string targetUhid,
        double delta,
        string reason = "test",
        long? timestampMsOverride = null)
    {
        var payload = new ReputationUpdatePayload
        {
            ReporterUhid = reporterUhid,
            TargetUhid   = targetUhid,
            ScoreDelta   = delta,
            TimestampMs  = timestampMsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Reason       = reason,
        };
        return new MeshPacket
        {
            Type            = PacketType.ReputationUpdate,
            SourceUhid      = reporterUhid,
            DestinationUhid = "*",
            PacketNonce     = new byte[8],
            TimestampMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Signature       = new byte[64],
            Payload         = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };
    }

    // ── factory ───────────────────────────────────────────────────────────────

    private static (ReputationGossipService svc, FakeMeshSender sender, FakePacketSigningService signing, InMemoryNodeReputationService reputation)
        Build(string localUhid = "aether:local:01")
    {
        var sender = new FakeMeshSender { LocalUhid = localUhid };
        var signing = new FakePacketSigningService();
        var reputation = new InMemoryNodeReputationService();
        var svc = new ReputationGossipService(sender, signing, reputation, NullLogger<ReputationGossipService>.Instance);
        return (svc, sender, signing, reputation);
    }

    // ── broadcast tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task BroadcastAsync_SignsAndBroadcastsOnePacket()
    {
        var (svc, sender, _, _) = Build();

        await svc.BroadcastReputationUpdateAsync("aether:target:02", -0.20, "sig_failure");

        Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.ReputationUpdate, sender.Broadcasts[0].Type);
    }

    [Fact]
    public async Task BroadcastAsync_PayloadHasCorrectFields()
    {
        var (svc, sender, _, _) = Build("aether:local:01");

        await svc.BroadcastReputationUpdateAsync("aether:target:02", -0.15, "replay_attack");

        var payload = JsonSerializer.Deserialize<ReputationUpdatePayload>(
            sender.Broadcasts[0].Payload, JsonOpts)!;
        Assert.Equal("aether:local:01", payload.ReporterUhid);
        Assert.Equal("aether:target:02", payload.TargetUhid);
        Assert.Equal(-0.15, payload.ScoreDelta, precision: 6);
        Assert.Equal("replay_attack", payload.Reason);
    }

    [Fact]
    public async Task BroadcastAsync_ClampsDeltaAboveOne()
    {
        var (svc, sender, _, _) = Build();
        await svc.BroadcastReputationUpdateAsync("aether:target:02", 5.0, "invalid");
        var payload = JsonSerializer.Deserialize<ReputationUpdatePayload>(
            sender.Broadcasts[0].Payload, JsonOpts)!;
        Assert.Equal(1.0, payload.ScoreDelta, precision: 6);
    }

    [Fact]
    public async Task BroadcastAsync_ClampsDeltaBelowMinusOne()
    {
        var (svc, sender, _, _) = Build();
        await svc.BroadcastReputationUpdateAsync("aether:target:02", -9.0, "invalid");
        var payload = JsonSerializer.Deserialize<ReputationUpdatePayload>(
            sender.Broadcasts[0].Payload, JsonOpts)!;
        Assert.Equal(-1.0, payload.ScoreDelta, precision: 6);
    }

    // ── handle tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleGossip_InvalidSignature_ReturnsFalse()
    {
        var (svc, _, signing, _) = Build();
        signing.VerifyResult = false;
        var pkt = MakeValidGossipPacket("aether:reporter:03", "aether:target:04", -0.20);

        Assert.False(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
    }

    [Fact]
    public async Task HandleGossip_WrongPacketType_ReturnsFalse()
    {
        var (svc, _, _, _) = Build();
        var pkt = MakeValidGossipPacket("aether:reporter:03", "aether:target:04", -0.20);
        pkt.Type = PacketType.Data;

        Assert.False(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
    }

    [Fact]
    public async Task HandleGossip_StalePayloadTimestamp_ReturnsFalse()
    {
        var (svc, _, _, _) = Build();
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeMilliseconds();
        var pkt = MakeValidGossipPacket("aether:reporter:03", "aether:target:04", -0.20,
            timestampMsOverride: staleTs);

        Assert.False(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
    }

    [Fact]
    public async Task HandleGossip_MissingReporterUhid_ReturnsFalse()
    {
        var (svc, _, _, _) = Build();
        var pkt = MakeValidGossipPacket("aether:reporter:03", "aether:target:04", -0.20);
        var badPayload = new ReputationUpdatePayload
        {
            ReporterUhid = string.Empty,
            TargetUhid   = "aether:target:04",
            ScoreDelta   = -0.20,
            TimestampMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        pkt.Payload = JsonSerializer.SerializeToUtf8Bytes(badPayload, JsonOpts);

        Assert.False(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
    }

    [Fact]
    public async Task HandleGossip_OwnGossip_ReturnsFalse()
    {
        var (svc, _, _, _) = Build("aether:local:01");
        var pkt = MakeValidGossipPacket("aether:local:01", "aether:target:04", -0.20);

        Assert.False(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
    }

    [Fact]
    public async Task HandleGossip_UnknownReporter_AppliesFullDelta()
    {
        var (svc, _, _, reputation) = Build();
        var pkt = MakeValidGossipPacket("aether:reporter:03", "aether:target:04", -0.20);

        Assert.True(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
        // target: 1.0 + (-0.20 × 1.0) = 0.80
        Assert.Equal(0.80, await reputation.GetReputationScoreAsync("aether:target:04"), precision: 6);
    }

    [Fact]
    public async Task HandleGossip_FullyTrustedReporter_AppliesFullDelta()
    {
        var (svc, _, _, reputation) = Build();
        var pkt = MakeValidGossipPacket("aether:reporter:03", "aether:target:04", -0.15);

        Assert.True(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
        Assert.Equal(0.85, await reputation.GetReputationScoreAsync("aether:target:04"), precision: 6);
    }

    [Fact]
    public async Task HandleGossip_DegradedReporter_AppliesWeightedDelta()
    {
        // 10× rreq flood: 10 × −0.05 = −0.50 → reporter R = 0.50
        var (svc, _, _, reputation) = Build();
        for (var i = 0; i < 10; i++)
            await reputation.RecordRreqFloodAttemptAsync("aether:reporter:05");

        var pkt = MakeValidGossipPacket("aether:reporter:05", "aether:target:06", -0.20);
        Assert.True(await svc.HandleGossipPacketAsync(pkt, new byte[32]));

        // effective = -0.20 × 0.50 = -0.10 → target = 1.0 - 0.10 = 0.90
        Assert.Equal(0.50, await reputation.GetReputationScoreAsync("aether:reporter:05"), precision: 6);
        Assert.Equal(0.90, await reputation.GetReputationScoreAsync("aether:target:06"), precision: 6);
    }

    [Fact]
    public async Task HandleGossip_UntrustedReporter_AppliesScaledDown()
    {
        // 4× sig failure: 4 × −0.20 = −0.80 → reporter R = 0.20
        var (svc, _, _, reputation) = Build();
        for (var i = 0; i < 4; i++)
            await reputation.RecordSignatureFailureAsync("aether:reporter:07");

        var pkt = MakeValidGossipPacket("aether:reporter:07", "aether:target:08", -0.20);
        Assert.True(await svc.HandleGossipPacketAsync(pkt, new byte[32]));

        // effective = -0.20 × 0.20 = -0.04 → target = 1.0 - 0.04 = 0.96
        Assert.Equal(0.20, await reputation.GetReputationScoreAsync("aether:reporter:07"), precision: 6);
        Assert.Equal(0.96, await reputation.GetReputationScoreAsync("aether:target:08"), precision: 6);
    }

    [Fact]
    public async Task HandleGossip_PositiveDelta_ImprovesTarget()
    {
        var (svc, _, _, reputation) = Build();
        await reputation.RecordSignatureFailureAsync("aether:target:09"); // target at 0.80
        var pkt = MakeValidGossipPacket("aether:reporter:10", "aether:target:09", +0.10, "good_behavior");

        Assert.True(await svc.HandleGossipPacketAsync(pkt, new byte[32]));
        // 0.80 + (0.10 × 1.0) = 0.90
        Assert.Equal(0.90, await reputation.GetReputationScoreAsync("aether:target:09"), precision: 6);
    }
}
